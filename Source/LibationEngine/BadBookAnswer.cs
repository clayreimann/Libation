using LibationFileManager;

namespace LibationEngine;

public sealed record BadBookAnswer(Configuration.BadBookAction Action, bool ApplyToAll, bool RememberInSettings);
