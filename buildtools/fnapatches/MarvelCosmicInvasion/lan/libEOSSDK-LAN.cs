// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Jeod
//
// LAN lobby and transport core behind the EOS shim
// Kept free of Epic types so it can become a native lib shared by other EOS ports
//
// One UDP socket does everything:
//   host    broadcasts a lobby snapshot beacon every 1s (id, members, attributes)
//   client  unicasts a JOIN heartbeat to the host every 1s (name, platform, member attrs)
//   both    exchange game packets (PKT unreliable, RPK/ACK reliable-ordered)
// A peer is dropped after 5s of silence, or once a reliable packet to it goes unacked for 10s
//
// Wire messages are "MCILAN2:" + ':'-delimited fields, base64 where content is free-form:
//   LOBBY:<lobbyId>:<hostId>:<snapshotB64>
//   JOIN:<lobbyId>:<clientId>:<session>:<nameB64>:<plat>:<attrsB64>
//   LEAVE:<lobbyId>:<clientId>:<session>
//   KICK:<lobbyId>:<clientId>:<session>
//   CLOSE:<lobbyId>:<hostId>
//   PKT:<fromId>:<channel>:<socketB64>:<payload>
//   RPK:<fromId>:<channel>:<socketB64>:<epoch>:<seq>:<payload>
//   ACK:<ackerId>:<epoch>:<seq>
// PKT and RPK payloads are raw bytes after the last field, so a full game packet fits one datagram
// Sessions number each join and epochs each reliable stream, so a rejoin starts both sides afresh
// Snapshot records: H|max|bucketB64  M|id|session|nameB64|plat|addr|attrsB64  A|key|type|valueB64
// Attribute blobs: newline-separated key|type|valueB64 records, base64'd as a whole
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Paris.Engine.EOSLan
{
	// An EOS attribute value. Only the field matching Type is meaningful
	class LanAttr
	{
		public char Type;   // 'i' int64, 'b' bool, 'd' double, 's' string
		public long IntValue;
		public bool BoolValue;
		public double DoubleValue;
		public string StringValue;

		public static LanAttr Int(long value) { return new LanAttr { Type = 'i', IntValue = value }; }
		public static LanAttr Bool(bool value) { return new LanAttr { Type = 'b', BoolValue = value }; }
		public static LanAttr Dbl(double value) { return new LanAttr { Type = 'd', DoubleValue = value }; }
		public static LanAttr Str(string value) { return new LanAttr { Type = 's', StringValue = value ?? "" }; }

		public string ToWireString()
		{
			switch (Type)
			{
				case 'i': return IntValue.ToString(CultureInfo.InvariantCulture);
				case 'b': return BoolValue ? "1" : "0";
				case 'd': return DoubleValue.ToString("R", CultureInfo.InvariantCulture);
				default: return StringValue ?? "";
			}
		}

		public static LanAttr Parse(char type, string text)
		{
			CultureInfo invariant = CultureInfo.InvariantCulture;
			switch (type)
			{
				case 'i':
				{
					long value;
					long.TryParse(text, NumberStyles.Integer, invariant, out value);
					return Int(value);
				}
				case 'b':
					return Bool(text == "1" || text == "true");
				case 'd':
				{
					double value;
					double.TryParse(text, NumberStyles.Float, invariant, out value);
					return Dbl(value);
				}
				default:
					return Str(text);
			}
		}
	}

	class LanMember
	{
		public string Id;
		public string Name = "";
		public int Platform;
		public int Session;
		public IPEndPoint Endpoint;    // the host learns it from JOINs, clients from the snapshot
		public long LastSeenMs;        // host only, for timeout eviction
		public readonly Dictionary<string, LanAttr> Attributes = new Dictionary<string, LanAttr>();   // GUESTS gates the game's member sync

		public LanMember Clone()
		{
			var copy = new LanMember { Id = Id, Name = Name, Platform = Platform, Session = Session, Endpoint = Endpoint, LastSeenMs = LastSeenMs };
			foreach (var attr in Attributes) copy.Attributes[attr.Key] = attr.Value;
			return copy;
		}
	}

	class LanPacket
	{
		public string FromId;
		public byte Channel;
		public string Socket;
		public byte[] Data;
	}

	// Per peer: resend until acked, deliver in order exactly once
	class ReliableStream
	{
		public int Epoch;
		public int NextOutgoingSeq = 1;
		public readonly Dictionary<int, PendingSend> Unacked = new Dictionary<int, PendingSend>();
		public int PeerEpoch;
		public int NextExpectedSeq = 1;
		public readonly Dictionary<int, LanPacket> OutOfOrder = new Dictionary<int, LanPacket>();
	}

	class PendingSend
	{
		public byte[] Datagram;
		public long FirstSentMs, LastSentMs;
	}

	class LanLobbyInfo
	{
		public string LobbyId;
		public string HostId;
		public uint MaxMembers = 4;
		public string Bucket = "";
		public readonly List<LanMember> Members = new List<LanMember>();   // host first
		public readonly Dictionary<string, LanAttr> Attributes = new Dictionary<string, LanAttr>();
	}

	// Lobby changes gathered under the state lock, raised once it is released
	class LanChanges
	{
		public readonly List<string> Left = new List<string>();
		public readonly List<string> Joined = new List<string>();
		public readonly List<string> Changed = new List<string>();
		public bool LobbyChanged, Lost, Kicked;
	}

	class MciLanNet
	{
		public const int PORT = 55123;
		const string Prefix = "MCILAN2:";
		const int TxTickMs = 200;          // resend granularity
		const int HeartbeatMs = 1000;      // beacon and JOIN heartbeat cadence
		const int MemberTimeoutMs = 5000;
		const int ResendMs = 250;
		const int ReliableGiveUpMs = 10000;
		const int OutOfOrderBufferCap = 256;
		const int InboxFloodCap = 512;     // unreliable packets stop here, so reliable ones keep headroom
		const int ReliableInboxCap = 2048;

		static readonly byte[] PrefixBytes = Encoding.ASCII.GetBytes(Prefix);

		readonly object stateLock = new object();
		UdpClient socket;
		List<IPEndPoint> broadcastTargets;
		long clockBaseTicks;    // Environment.TickCount can wrap; we only need deltas

		public string LocalId, LocalName = "Player";
		public int LocalPlatform = 258;   // Platforms.Steam | Platforms.Windows
		readonly Dictionary<string, LanAttr> localMemberAttributes = new Dictionary<string, LanAttr>();
		int localSession, lastSession;

		public bool IsHost, InLobby;
		public string LobbyId, HostId;
		public uint MaxMembers = 4;
		public string Bucket = "";
		readonly List<LanMember> members = new List<LanMember>();   // host first
		readonly Dictionary<string, LanAttr> lobbyAttributes = new Dictionary<string, LanAttr>();

		// Host side: "id/session" of each removed member, true if kicked, so its heartbeats can't re-add it
		readonly Dictionary<string, bool> droppedSessions = new Dictionary<string, bool>();

		// Client side
		IPEndPoint hostEndpoint;
		long hostLastSeenMs;
		public bool JoinAcknowledged;

		// Learned from any datagram, so replies needn't wait for membership to propagate
		readonly Dictionary<string, IPEndPoint> peerEndpoints = new Dictionary<string, IPEndPoint>();

		bool searching;
		string searchFilterId;    // non-null => join-by-code
		readonly Dictionary<string, LanLobbyInfo> searchResults = new Dictionary<string, LanLobbyInfo>();

		// Raised on the network threads, or the caller's for Kick; the adapter re-posts them onto Platform.Tick
		public event Action<string> MemberJoined;
		public event Action<string> MemberLeft;
		public event Action LobbyChanged;
		public event Action<string> MemberChanged;   // a member's attributes changed
		public event Action<bool> LobbyLost;         // the lobby dropped us, true if kicked

		readonly Queue<LanPacket> inbox = new Queue<LanPacket>();
		readonly Dictionary<string, ReliableStream> reliableStreams = new Dictionary<string, ReliableStream>();
		int lastEpoch;

		long NowMs() { return (Environment.TickCount - clockBaseTicks) & 0x7fffffffL; }

		// Hostnames repeat across devices (every ROCKNIX unit is "ROCKNIX"), hence the per-process suffix
		public static string NewLocalId(string name)
		{
			return "LAN-" + name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
		}

		public void Start()
		{
			clockBaseTicks = Environment.TickCount;
			var udp = new UdpClient();
			try
			{
				udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
				udp.EnableBroadcast = true;
				udp.Client.Bind(new IPEndPoint(IPAddress.Any, PORT));
			}
			catch
			{
				udp.Close();
				throw;
			}
			socket = udp;
			broadcastTargets = FindBroadcastTargets(PORT);
			new Thread(ReceiveLoop) { IsBackground = true, Name = "MciLanRx" }.Start();
			new Thread(SendLoop) { IsBackground = true, Name = "MciLanTx" }.Start();
		}

		public string CreateLobby(string lobbyId, uint maxMembers, string bucket)
		{
			lock (stateLock)
			{
				IsHost = true; InLobby = true;
				LobbyId = string.IsNullOrEmpty(lobbyId) ? GenerateLobbyCode() : lobbyId;
				HostId = LocalId;
				MaxMembers = maxMembers > 0 ? maxMembers : 4;
				Bucket = bucket ?? "";
				ClearLobbyState();
				localSession = ++lastSession;
				LanMember self = new LanMember { Id = LocalId, Name = LocalName, Platform = LocalPlatform, Session = localSession };
				foreach (var attr in localMemberAttributes) self.Attributes[attr.Key] = attr.Value;
				members.Add(self);
			}
			return LobbyId;
		}

		string GenerateLobbyCode()
		{
			// Short and typeable, for "Join Party Code"
			const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
			var builder = new StringBuilder();
			int seed = Environment.TickCount ^ (LocalId != null ? LocalId.GetHashCode() : 0);
			for (int i = 0; i < 6; i++)
			{
				seed = seed * 1103515245 + 12345;
				builder.Append(Alphabet[((seed >> 16) & 0x7fff) % Alphabet.Length]);
			}
			return builder.ToString();
		}

		public void SetLobbyAttr(string key, LanAttr value) { lock (stateLock) lobbyAttributes[key] = value; }
		public void SetMaxMembers(uint max) { lock (stateLock) MaxMembers = max; }
		public void SetBucket(string bucket) { lock (stateLock) Bucket = bucket ?? ""; }

		public void SetLocalName(string name)
		{
			if (string.IsNullOrEmpty(name)) return;
			lock (stateLock)
			{
				LocalName = name;
				LanMember self = members.Find(candidate => candidate.Id == LocalId);
				if (self != null) self.Name = name;
			}
		}

		public void SetLocalMemberAttrs(List<KeyValuePair<string, LanAttr>> pairs)
		{
			if (pairs == null || pairs.Count == 0) return;
			lock (stateLock)
			{
				foreach (var pair in pairs) localMemberAttributes[pair.Key] = pair.Value;
				LanMember self = members.Find(candidate => candidate.Id == LocalId);
				if (self != null)
					foreach (var pair in pairs) self.Attributes[pair.Key] = pair.Value;
			}
		}

		// Copies, since the receive thread rewrites member attributes in place
		public List<LanMember> Members()
		{
			lock (stateLock)
			{
				var copies = new List<LanMember>(members.Count);
				foreach (var member in members) copies.Add(member.Clone());
				return copies;
			}
		}

		public Dictionary<string, LanAttr> Attributes() { lock (stateLock) { return new Dictionary<string, LanAttr>(lobbyAttributes); } }

		public void StartSearch(string filterLobbyId)
		{
			lock (stateLock) { searching = true; searchFilterId = filterLobbyId; searchResults.Clear(); }
		}

		public List<LanLobbyInfo> SearchResults()
		{
			lock (stateLock) { return new List<LanLobbyInfo>(searchResults.Values); }
		}

		// Takes a frozen search result, so a search refresh can't invalidate it
		public bool Join(LanLobbyInfo info)
		{
			if (info == null || string.IsNullOrEmpty(info.LobbyId)) return false;
			IPEndPoint host;
			string joinMessage;
			lock (stateLock)
			{
				IsHost = false; InLobby = true; JoinAcknowledged = false;
				LobbyId = info.LobbyId; HostId = info.HostId;
				MaxMembers = info.MaxMembers; Bucket = info.Bucket;
				hostEndpoint = info.Members.Count > 0 ? info.Members[0].Endpoint : null;
				hostLastSeenMs = NowMs();
				ClearLobbyState();
				localSession = ++lastSession;
				foreach (var source in info.Members)
				{
					members.Add(source.Clone());
					if (source.Endpoint != null) peerEndpoints[source.Id] = source.Endpoint;
				}
				foreach (var attr in info.Attributes) lobbyAttributes[attr.Key] = attr.Value;
				searching = false;
				host = hostEndpoint;
				joinMessage = JoinMessage();
			}
			// Announce now, since the game sends packets right after joining
			if (host != null) Send(joinMessage, host);
			return true;
		}

		public void Leave()
		{
			string message;
			var destinations = new List<IPEndPoint>();
			lock (stateLock)
			{
				if (!InLobby) return;
				if (IsHost)
				{
					// Broadcast as well, so searchers drop the lobby too
					message = Prefix + "CLOSE:" + LobbyId + ":" + LocalId;
					destinations.AddRange(broadcastTargets);
					foreach (var member in members)
						if (member.Id != LocalId && member.Endpoint != null) destinations.Add(member.Endpoint);
				}
				else
				{
					message = Prefix + "LEAVE:" + LobbyId + ":" + LocalId + ":" + localSession;
					if (hostEndpoint != null) destinations.Add(hostEndpoint);
				}
				ClearLobby();
			}
			Send(message, destinations);
		}

		// Host only. The caller reports the member as kicked, so MemberLeft isn't raised
		public bool Kick(string memberId)
		{
			IPEndPoint dest; string message;
			lock (stateLock)
			{
				int index = (InLobby && IsHost) ? members.FindIndex(candidate => candidate.Id == memberId) : -1;
				// Index 0 is the host itself
				if (index <= 0) return false;
				dest = members[index].Endpoint;
				message = KickMessage(memberId, members[index].Session);
				DropMember(index, true);
			}
			if (dest != null) Send(message, dest);
			if (LobbyChanged != null) LobbyChanged();
			return true;
		}

		// Caller holds stateLock
		void ClearLobby()
		{
			InLobby = false; IsHost = false; JoinAcknowledged = false;
			ClearLobbyState();
			LobbyId = null; HostId = null; hostEndpoint = null;
		}

		// Caller holds stateLock
		void ClearLobbyState()
		{
			members.Clear(); lobbyAttributes.Clear(); peerEndpoints.Clear(); reliableStreams.Clear(); inbox.Clear(); droppedSessions.Clear();
		}

		// Caller holds stateLock
		void LoseLobby(LanChanges changes)
		{
			ClearLobby();
			changes.Lost = true;
		}

		// Caller holds stateLock
		void DropMember(int index, bool kicked)
		{
			LanMember member = members[index];
			members.RemoveAt(index);
			droppedSessions[member.Id + "/" + member.Session] = kicked;
			reliableStreams.Remove(member.Id); peerEndpoints.Remove(member.Id);
		}

		void RaiseChanges(LanChanges changes)
		{
			if (MemberLeft != null) foreach (string id in changes.Left) MemberLeft(id);
			if (MemberJoined != null) foreach (string id in changes.Joined) MemberJoined(id);
			if ((changes.LobbyChanged || changes.Left.Count > 0 || changes.Joined.Count > 0) && LobbyChanged != null) LobbyChanged();
			if (MemberChanged != null) foreach (string id in changes.Changed) MemberChanged(id);
			if (changes.Lost && LobbyLost != null) LobbyLost(changes.Kicked);
		}

		// Caller holds stateLock
		string JoinMessage()
		{
			return Prefix + "JOIN:" + LobbyId + ":" + LocalId + ":" + localSession + ":" + ToBase64(LocalName) + ":" + LocalPlatform + ":" + SerializeAttributes(localMemberAttributes);
		}

		// Caller holds stateLock
		string KickMessage(string clientId, int session)
		{
			return Prefix + "KICK:" + LobbyId + ":" + clientId + ":" + session;
		}

		// Caller holds stateLock
		IPEndPoint ResolveEndpoint(string peerId)
		{
			LanMember member = members.Find(candidate => candidate.Id == peerId);
			if (member != null && member.Endpoint != null) return member.Endpoint;
			IPEndPoint known;
			if (peerEndpoints.TryGetValue(peerId, out known)) return known;
			if (!IsHost && peerId == HostId) return hostEndpoint;
			return null;
		}

		// Caller holds stateLock
		ReliableStream StreamFor(string peerId)
		{
			ReliableStream stream;
			if (!reliableStreams.TryGetValue(peerId, out stream)) { stream = new ReliableStream { Epoch = ++lastEpoch }; reliableStreams[peerId] = stream; }
			return stream;
		}

		public bool SendPacket(string toId, byte channel, string socketName, byte[] data)
		{
			if (string.IsNullOrEmpty(toId) || data == null) return false;
			IPEndPoint dest;
			lock (stateLock) { dest = ResolveEndpoint(toId); }
			if (dest == null) return false;
			Send(Frame(Prefix + "PKT:" + LocalId + ":" + channel + ":" + ToBase64(socketName) + ":", data), dest);
			return true;
		}

		public bool SendPacketReliable(string toId, byte channel, string socketName, byte[] data)
		{
			if (string.IsNullOrEmpty(toId) || data == null) return false;
			IPEndPoint dest; byte[] datagram;
			lock (stateLock)
			{
				dest = ResolveEndpoint(toId);
				if (dest == null) return false;
				ReliableStream stream = StreamFor(toId);
				int seq = stream.NextOutgoingSeq++;
				datagram = Frame(Prefix + "RPK:" + LocalId + ":" + channel + ":" + ToBase64(socketName) + ":" + stream.Epoch + ":" + seq + ":", data);
				long now = NowMs();
				stream.Unacked[seq] = new PendingSend { Datagram = datagram, FirstSentMs = now, LastSentMs = now };
			}
			Send(datagram, dest);
			return true;
		}

		public LanPacket PeekPacket() { lock (stateLock) { return inbox.Count > 0 ? inbox.Peek() : null; } }
		public LanPacket DequeuePacket() { lock (stateLock) { return inbox.Count > 0 ? inbox.Dequeue() : null; } }

		void ResendUnacked()
		{
			List<KeyValuePair<IPEndPoint, byte[]>> due = null;
			var changes = new LanChanges();
			lock (stateLock)
			{
				long now = NowMs();
				List<string> unresponsive = null;
				foreach (var stream in reliableStreams)
				{
					IPEndPoint dest = ResolveEndpoint(stream.Key);
					foreach (var pending in stream.Value.Unacked.Values)
					{
						if (now - pending.FirstSentMs > ReliableGiveUpMs)
						{
							if (unresponsive == null) unresponsive = new List<string>();
							unresponsive.Add(stream.Key);
							break;
						}
						if (dest != null && now - pending.LastSentMs >= ResendMs)
						{
							pending.LastSentMs = now;
							if (due == null) due = new List<KeyValuePair<IPEndPoint, byte[]>>();
							due.Add(new KeyValuePair<IPEndPoint, byte[]>(dest, pending.Datagram));
						}
					}
				}
				if (unresponsive != null)
					foreach (string peerId in unresponsive) DropUnresponsive(peerId, changes);
			}
			RaiseChanges(changes);
			if (due == null) return;
			foreach (var send in due) Send(send.Value, send.Key);
		}

		// Caller holds stateLock
		void DropUnresponsive(string peerId, LanChanges changes)
		{
			// Clients don't own membership, so a stream to another client just restarts
			reliableStreams.Remove(peerId);
			if (!InLobby) return;
			if (IsHost)
			{
				int index = members.FindIndex(candidate => candidate.Id == peerId);
				if (index > 0) { changes.Left.Add(peerId); DropMember(index, false); }
			}
			else if (peerId == HostId) LoseLobby(changes);
		}

		void HandleReliablePacket(byte[] datagram, IPEndPoint from)
		{
			string[] fields;
			LanPacket packet = Unframe(datagram, 6, out fields);
			if (packet == null) return;
			int epoch, seq;
			if (!int.TryParse(fields[4], out epoch) || !int.TryParse(fields[5], out seq)) return;
			bool accepted;
			lock (stateLock)
			{
				if (!InLobby) return;
				Touch(packet.FromId, from);
				ReliableStream stream = StreamFor(packet.FromId);
				// Left over from a stream the peer has since restarted
				if (epoch < stream.PeerEpoch) return;
				if (epoch > stream.PeerEpoch) { stream.PeerEpoch = epoch; stream.NextExpectedSeq = 1; stream.OutOfOrder.Clear(); }
				accepted = AcceptReliable(stream, seq, packet);
			}
			// Ack only what was accepted, so anything refused gets resent
			if (accepted) Send(Prefix + "ACK:" + LocalId + ":" + epoch + ":" + seq, new IPEndPoint(from.Address, PORT));
		}

		// Caller holds stateLock
		bool AcceptReliable(ReliableStream stream, int seq, LanPacket packet)
		{
			// A duplicate means our last ack was lost
			if (seq < stream.NextExpectedSeq || stream.OutOfOrder.ContainsKey(seq)) return true;
			if (seq > stream.NextExpectedSeq)
			{
				if (stream.OutOfOrder.Count >= OutOfOrderBufferCap) return false;
				stream.OutOfOrder[seq] = packet;
				return true;
			}
			if (inbox.Count >= ReliableInboxCap) return false;
			inbox.Enqueue(packet);
			stream.NextExpectedSeq++;
			// Buffered packets are acked already, so they go in even past the cap
			LanPacket buffered;
			while (stream.OutOfOrder.TryGetValue(stream.NextExpectedSeq, out buffered))
			{
				stream.OutOfOrder.Remove(stream.NextExpectedSeq);
				inbox.Enqueue(buffered);
				stream.NextExpectedSeq++;
			}
			return true;
		}

		void HandleAck(string body, IPEndPoint from)
		{
			string[] fields = body.Split(':');
			if (fields.Length < 4) return;
			int epoch, seq;
			if (!int.TryParse(fields[2], out epoch) || !int.TryParse(fields[3], out seq)) return;
			lock (stateLock)
			{
				if (!InLobby) return;
				Touch(fields[1], from);
				ReliableStream stream;
				if (reliableStreams.TryGetValue(fields[1], out stream) && stream.Epoch == epoch) stream.Unacked.Remove(seq);
			}
		}

		void HandlePacket(byte[] datagram, IPEndPoint from)
		{
			string[] fields;
			LanPacket packet = Unframe(datagram, 4, out fields);
			if (packet == null) return;
			lock (stateLock)
			{
				if (!InLobby) return;
				Touch(packet.FromId, from);
				// Drop the newcomer, since anything queued may be an acked reliable packet
				if (inbox.Count < InboxFloodCap) inbox.Enqueue(packet);
			}
		}

		// Caller holds stateLock. Any datagram counts as a heartbeat
		void Touch(string peerId, IPEndPoint from)
		{
			var endpoint = new IPEndPoint(from.Address, PORT);
			peerEndpoints[peerId] = endpoint;
			LanMember member = members.Find(candidate => candidate.Id == peerId);
			if (member != null) { member.LastSeenMs = NowMs(); if (member.Endpoint == null) member.Endpoint = endpoint; }
			if (peerId == HostId) hostLastSeenMs = NowMs();
		}

		void SendLoop()
		{
			int ticksSinceHeartbeat = 0;
			while (true)
			{
				try
				{
					ResendUnacked();
					if (++ticksSinceHeartbeat * TxTickMs >= HeartbeatMs)
					{
						ticksSinceHeartbeat = 0;
						Heartbeat();
					}
				}
				catch { }
				Thread.Sleep(TxTickMs);
			}
		}

		void Heartbeat()
		{
			bool hosting;
			lock (stateLock) hosting = InLobby && IsHost;
			// Re-read each time, since Wi-Fi may come up after Start
			List<IPEndPoint> targets = hosting ? FindBroadcastTargets(PORT) : null;
			string message = null; List<IPEndPoint> destinations = null;
			var changes = new LanChanges();
			lock (stateLock)
			{
				if (targets != null) broadcastTargets = targets;
				if (InLobby && IsHost)
				{
					EvictStaleMembers(changes);
					// Wait for the lobby attributes, since clients check VERSION, DLC and
					// bucket from the snapshot
					if (lobbyAttributes.Count > 0)
					{
						message = Prefix + "LOBBY:" + LobbyId + ":" + HostId + ":" + BuildSnapshot();
						destinations = broadcastTargets;
					}
				}
				else if (InLobby)
				{
					if (NowMs() - hostLastSeenMs > MemberTimeoutMs) LoseLobby(changes);
					else if (hostEndpoint != null)
					{
						message = JoinMessage();
						destinations = new List<IPEndPoint> { hostEndpoint };
					}
				}
			}
			RaiseChanges(changes);
			if (message != null) Send(message, destinations);
		}

		// Caller holds stateLock
		void EvictStaleMembers(LanChanges changes)
		{
			long now = NowMs();
			// Never evict the host at index 0
			for (int i = members.Count - 1; i >= 1; i--)
			{
				if (now - members[i].LastSeenMs > MemberTimeoutMs)
				{
					changes.Left.Add(members[i].Id);
					DropMember(i, false);
				}
			}
		}

		void ReceiveLoop()
		{
			while (true)
			{
				try
				{
					var from = new IPEndPoint(IPAddress.Any, 0);
					byte[] datagram = socket.Receive(ref from);
					if (!HasPrefix(datagram)) continue;
					int colon = Array.IndexOf(datagram, (byte)':', PrefixBytes.Length);
					if (colon < 0) continue;
					string verb = Encoding.ASCII.GetString(datagram, PrefixBytes.Length, colon - PrefixBytes.Length);
					if (verb == "PKT") HandlePacket(datagram, from);
					else if (verb == "RPK") HandleReliablePacket(datagram, from);
					else
					{
						string body = Encoding.UTF8.GetString(datagram, PrefixBytes.Length, datagram.Length - PrefixBytes.Length);
						if (verb == "ACK") HandleAck(body, from);
						else if (verb == "LOBBY") HandleBeacon(body, from);
						else if (verb == "JOIN") HandleJoin(body, from);
						else if (verb == "LEAVE") HandleLeave(body);
						else if (verb == "KICK") HandleKick(body);
						else if (verb == "CLOSE") HandleClose(body);
					}
				}
				catch (Exception) { Thread.Sleep(200); }
			}
		}

		void HandleBeacon(string body, IPEndPoint from)
		{
			string[] fields = body.Split(new char[] { ':' }, 4);
			if (fields.Length < 4) return;
			string lobbyId = fields[1], hostId = fields[2];
			// Our own beacon
			if (hostId == LocalId) return;
			LanLobbyInfo info = ParseSnapshot(lobbyId, hostId, fields[3], from);
			var changes = new LanChanges();
			lock (stateLock)
			{
				if (searching && (searchFilterId == null || searchFilterId == lobbyId))
					searchResults[lobbyId] = info;
				if (InLobby && !IsHost && lobbyId == LobbyId && hostId == HostId)
					AdoptSnapshot(info, changes);
			}
			RaiseChanges(changes);
		}

		// Caller holds stateLock. Diffs the host's snapshot against ours, then takes it
		void AdoptSnapshot(LanLobbyInfo info, LanChanges changes)
		{
			hostLastSeenMs = NowMs();
			bool amMember = info.Members.Exists(member => member.Id == LocalId && member.Session == localSession);
			if (JoinAcknowledged && !amMember)
			{
				// The host dropped us, e.g. a timeout, or a kick whose KICK was lost
				LoseLobby(changes);
				return;
			}
			if (amMember && !JoinAcknowledged) { JoinAcknowledged = true; changes.LobbyChanged = true; }
			var previousById = new Dictionary<string, LanMember>();
			foreach (var member in members)
				if (member.Id != LocalId) previousById[member.Id] = member;
			foreach (var member in info.Members)
			{
				if (member.Id == LocalId) continue;
				LanMember previous;
				if (!previousById.TryGetValue(member.Id, out previous)) { changes.Joined.Add(member.Id); continue; }
				previousById.Remove(member.Id);
				if (previous.Session != member.Session)
				{
					// Left and came back between beacons
					changes.Left.Add(member.Id); changes.Joined.Add(member.Id);
					reliableStreams.Remove(member.Id);
				}
				else if (!AttributesEqual(previous.Attributes, member.Attributes))
					changes.Changed.Add(member.Id);
			}
			foreach (var gone in previousById.Values)
			{
				changes.Left.Add(gone.Id);
				reliableStreams.Remove(gone.Id); peerEndpoints.Remove(gone.Id);
			}
			if (MaxMembers != info.MaxMembers || Bucket != info.Bucket || !AttributesEqual(lobbyAttributes, info.Attributes))
				changes.LobbyChanged = true;
			// Copies, so the live list never shares objects with a search result
			members.Clear(); foreach (var member in info.Members) members.Add(member.Clone());
			lobbyAttributes.Clear(); foreach (var attr in info.Attributes) lobbyAttributes[attr.Key] = attr.Value;
			MaxMembers = info.MaxMembers; Bucket = info.Bucket;
			if (info.Members.Count > 0) hostEndpoint = info.Members[0].Endpoint ?? hostEndpoint;
			// Our own attributes win, since the beacon may predate our last heartbeat
			LanMember self = members.Find(candidate => candidate.Id == LocalId);
			if (self != null)
				foreach (var attr in localMemberAttributes) self.Attributes[attr.Key] = attr.Value;
		}

		void HandleJoin(string body, IPEndPoint from)
		{
			string[] fields = body.Split(':');
			if (fields.Length < 7) return;
			string lobbyId = fields[1], clientId = fields[2], name = FromBase64(fields[4]);
			int session; if (!int.TryParse(fields[3], out session)) return;
			int platform; int.TryParse(fields[5], out platform);
			Dictionary<string, LanAttr> memberAttributes = ParseAttributes(fields[6]);
			var changes = new LanChanges();
			string kickMessage = null;
			lock (stateLock)
			{
				if (!IsHost || !InLobby || lobbyId != LobbyId) return;
				bool kicked;
				if (droppedSessions.TryGetValue(clientId + "/" + session, out kicked))
				{
					// Repeat a KICK the client may have missed
					if (kicked) kickMessage = KickMessage(clientId, session);
				}
				else
				{
					int index = members.FindIndex(candidate => candidate.Id == clientId);
					// Back under a new session before the old one timed out
					if (index > 0 && members[index].Session != session) { changes.Left.Add(clientId); DropMember(index, false); index = -1; }
					if (index < 0 && members.Count < MaxMembers)
					{
						var member = new LanMember { Id = clientId, Name = name, Platform = platform, Session = session };
						foreach (var attr in memberAttributes) member.Attributes[attr.Key] = attr.Value;
						members.Add(member);
						changes.Joined.Add(clientId);
					}
					else if (index > 0)
					{
						LanMember member = members[index];
						member.Name = name; member.Endpoint = new IPEndPoint(from.Address, PORT);
						if (!AttributesEqual(member.Attributes, memberAttributes))
						{
							member.Attributes.Clear();
							foreach (var attr in memberAttributes) member.Attributes[attr.Key] = attr.Value;
							changes.Changed.Add(clientId);
						}
					}
					Touch(clientId, from);
				}
			}
			if (kickMessage != null) Send(kickMessage, new IPEndPoint(from.Address, PORT));
			RaiseChanges(changes);
		}

		void HandleLeave(string body)
		{
			string[] fields = body.Split(':');
			if (fields.Length < 4) return;
			string lobbyId = fields[1], clientId = fields[2];
			int session; if (!int.TryParse(fields[3], out session)) return;
			var changes = new LanChanges();
			lock (stateLock)
			{
				if (!IsHost || lobbyId != LobbyId) return;
				int index = members.FindIndex(candidate => candidate.Id == clientId && candidate.Session == session);
				if (index > 0) { changes.Left.Add(clientId); DropMember(index, false); }
			}
			RaiseChanges(changes);
		}

		void HandleKick(string body)
		{
			string[] fields = body.Split(':');
			if (fields.Length < 4) return;
			int session; if (!int.TryParse(fields[3], out session)) return;
			var changes = new LanChanges();
			lock (stateLock)
			{
				if (InLobby && !IsHost && fields[1] == LobbyId && fields[2] == LocalId && session == localSession)
				{
					LoseLobby(changes);
					changes.Kicked = true;
				}
			}
			RaiseChanges(changes);
		}

		void HandleClose(string body)
		{
			string[] fields = body.Split(':');
			if (fields.Length < 3) return;
			string lobbyId = fields[1], hostId = fields[2];
			var changes = new LanChanges();
			lock (stateLock)
			{
				LanLobbyInfo listed;
				if (searchResults.TryGetValue(lobbyId, out listed) && listed.HostId == hostId) searchResults.Remove(lobbyId);
				if (InLobby && !IsHost && lobbyId == LobbyId && hostId == HostId) LoseLobby(changes);
			}
			RaiseChanges(changes);
		}

		// Caller holds stateLock
		string BuildSnapshot()
		{
			var builder = new StringBuilder();
			builder.Append("H|").Append(MaxMembers).Append('|').Append(ToBase64(Bucket)).Append('\n');
			foreach (var member in members)
				builder.Append("M|").Append(member.Id).Append('|').Append(member.Session).Append('|').Append(ToBase64(member.Name)).Append('|').Append(member.Platform)
				       .Append('|').Append(member.Endpoint != null ? member.Endpoint.Address.ToString() : "-")
				       .Append('|').Append(SerializeAttributes(member.Attributes)).Append('\n');
			foreach (var attr in lobbyAttributes)
				builder.Append("A|").Append(attr.Key).Append('|').Append(attr.Value.Type).Append('|').Append(ToBase64(attr.Value.ToWireString())).Append('\n');
			return ToBase64(builder.ToString());
		}

		LanLobbyInfo ParseSnapshot(string lobbyId, string hostId, string snapshotB64, IPEndPoint from)
		{
			var info = new LanLobbyInfo { LobbyId = lobbyId, HostId = hostId };
			string snapshot = FromBase64(snapshotB64);
			bool firstMember = true;
			foreach (string line in snapshot.Split('\n'))
			{
				if (line.Length == 0) continue;
				string[] parts = line.Split('|');
				if (parts[0] == "H" && parts.Length >= 3)
				{
					uint.TryParse(parts[1], out info.MaxMembers);
					info.Bucket = FromBase64(parts[2]);
				}
				else if (parts[0] == "M" && parts.Length >= 5)
				{
					int session; int.TryParse(parts[2], out session);
					int platform; int.TryParse(parts[4], out platform);
					var member = new LanMember { Id = parts[1], Session = session, Name = FromBase64(parts[3]), Platform = platform };
					// The host publishes each member's address for client-to-client P2P
					if (parts.Length >= 6 && parts[5] != "-")
					{
						try { member.Endpoint = new IPEndPoint(IPAddress.Parse(parts[5]), PORT); } catch { }
					}
					if (parts.Length >= 7)
						foreach (var attr in ParseAttributes(parts[6])) member.Attributes[attr.Key] = attr.Value;
					// Member 0 is the host, and the beacon's source address beats the published one
					if (firstMember) { member.Endpoint = new IPEndPoint(from.Address, PORT); firstMember = false; }
					info.Members.Add(member);
				}
				else if (parts[0] == "A" && parts.Length >= 4)
					info.Attributes[parts[1]] = LanAttr.Parse(parts[2][0], FromBase64(parts[3]));
			}
			return info;
		}

		static string ToBase64(string text) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? "")); }
		static string FromBase64(string base64) { try { return Encoding.UTF8.GetString(Convert.FromBase64String(base64)); } catch { return ""; } }

		static string SerializeAttributes(Dictionary<string, LanAttr> attributes)
		{
			if (attributes == null || attributes.Count == 0) return ToBase64("");
			var builder = new StringBuilder();
			foreach (var attr in attributes)
				builder.Append(attr.Key).Append('|').Append(attr.Value.Type).Append('|').Append(ToBase64(attr.Value.ToWireString())).Append('\n');
			return ToBase64(builder.ToString());
		}

		static Dictionary<string, LanAttr> ParseAttributes(string blobB64)
		{
			var result = new Dictionary<string, LanAttr>();
			foreach (string line in FromBase64(blobB64).Split('\n'))
			{
				if (line.Length == 0) continue;
				string[] parts = line.Split('|');
				if (parts.Length >= 3 && parts[1].Length > 0)
					result[parts[0]] = LanAttr.Parse(parts[1][0], FromBase64(parts[2]));
			}
			return result;
		}

		static bool AttributesEqual(Dictionary<string, LanAttr> a, Dictionary<string, LanAttr> b)
		{
			if (a.Count != b.Count) return false;
			foreach (var entry in a)
			{
				LanAttr other;
				if (!b.TryGetValue(entry.Key, out other) || other.Type != entry.Value.Type || other.ToWireString() != entry.Value.ToWireString())
					return false;
			}
			return true;
		}

		static bool HasPrefix(byte[] datagram)
		{
			if (datagram.Length < PrefixBytes.Length) return false;
			for (int i = 0; i < PrefixBytes.Length; i++)
				if (datagram[i] != PrefixBytes[i]) return false;
			return true;
		}

		static byte[] Frame(string header, byte[] payload)
		{
			byte[] head = Encoding.UTF8.GetBytes(header);
			byte[] datagram = new byte[head.Length + payload.Length];
			Buffer.BlockCopy(head, 0, datagram, 0, head.Length);
			Buffer.BlockCopy(payload, 0, datagram, head.Length, payload.Length);
			return datagram;
		}

		// Splits off the verb and the header fields after it; the payload is whatever follows the last one
		static LanPacket Unframe(byte[] datagram, int fieldCount, out string[] fields)
		{
			fields = new string[fieldCount];
			int start = PrefixBytes.Length;
			for (int i = 0; i < fieldCount; i++)
			{
				int colon = Array.IndexOf(datagram, (byte)':', start);
				if (colon < 0) return null;
				fields[i] = Encoding.UTF8.GetString(datagram, start, colon - start);
				start = colon + 1;
			}
			byte channel; byte.TryParse(fields[2], out channel);
			var payload = new byte[datagram.Length - start];
			Buffer.BlockCopy(datagram, start, payload, 0, payload.Length);
			return new LanPacket { FromId = fields[1], Channel = channel, Socket = FromBase64(fields[3]), Data = payload };
		}

		void Send(string message, List<IPEndPoint> destinations)
		{
			byte[] datagram = Encoding.UTF8.GetBytes(message);
			foreach (var dest in destinations) Send(datagram, dest);
		}

		void Send(string message, IPEndPoint dest) { Send(Encoding.UTF8.GetBytes(message), dest); }

		void Send(byte[] datagram, IPEndPoint dest) { try { socket.Send(datagram, datagram.Length, dest); } catch { } }

		// Every up, non-loopback IPv4 interface's directed broadcast, plus the global
		// 255.255.255.255, to cover multi-subnet setups and odd adapters
		static List<IPEndPoint> FindBroadcastTargets(int port)
		{
			var targets = new List<IPEndPoint> { new IPEndPoint(IPAddress.Broadcast, port) };
			try
			{
				foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
				{
					if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
					foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
					{
						if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
						byte[] address = unicast.Address.GetAddressBytes();
						byte[] mask;
						try { mask = unicast.IPv4Mask.GetAddressBytes(); } catch { continue; }
						if (mask == null || mask.Length != 4 || (mask[0] | mask[1] | mask[2] | mask[3]) == 0) continue;
						byte[] broadcast = new byte[4];
						for (int i = 0; i < 4; i++) broadcast[i] = (byte)(address[i] | (~mask[i] & 0xff));
						targets.Add(new IPEndPoint(new IPAddress(broadcast), port));
					}
				}
			}
			catch { }
			return targets;
		}
	}
}
