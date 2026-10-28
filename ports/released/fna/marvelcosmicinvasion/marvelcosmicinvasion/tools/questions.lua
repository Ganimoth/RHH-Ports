-- The launcher sets MCI_ASK_* only for choices that weren't made on an earlier setup
local questions = {}

if os.getenv("MCI_ASK_INTRO") == "1" then
    table.insert(questions, {
        id = "ENC_INTRO",
        question = "Re-encode the intro movie for smoother playback on this device? This adds a few minutes to first-time setup; choosing No skips the intro instead.",
        options = {
            {"1", "Yes, re-encode (recommended)"},
            {"0", "No, skip the intro"}
        }
    })
end

if os.getenv("MCI_ASK_AOT") == "1" then
    table.insert(questions, {
        id = "AOT_EXTRA",
        question = "Spend extra time now so the game loads faster and stutters less? This adds several minutes to setup, most on slower devices.",
        options = {
            {"1", "Yes, take the extra time (recommended)"},
            {"0", "No, finish setup sooner"}
        }
    })
end

return questions
