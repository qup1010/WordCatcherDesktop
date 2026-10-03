using System;

namespace WordCatcher.Core.Models;

public sealed record CaptureResult(
    string SelectedText,
    string SourceProcess,
    string SourceWindowTitle,
    ScreenPoint CursorPosition,
    DateTimeOffset CapturedAt,
    string Sentence = "",
    int SentenceOffset = -1,
    string SourceUri = "");
