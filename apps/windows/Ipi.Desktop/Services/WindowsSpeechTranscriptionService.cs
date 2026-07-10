using System.Globalization;
using System.Text;
using Windows.Globalization;
using Windows.Media.SpeechRecognition;

namespace Ipi.Desktop.Services;

public sealed class WindowsSpeechTranscriptionService : IDisposable
{
    private readonly StringBuilder _buffer = new();
    private SpeechRecognizer? _recognizer;
    private bool _isRunning;

    public bool IsRunning => _isRunning;

    public async Task StartAsync()
    {
        if (_isRunning) return;

        _buffer.Clear();
        _recognizer = CreateRecognizer();
        _recognizer.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "dictation"));
        _recognizer.ContinuousRecognitionSession.ResultGenerated += OnResultGenerated;

        try
        {
            var compilation = await _recognizer.CompileConstraintsAsync();
            if (compilation.Status != SpeechRecognitionResultStatus.Success)
            {
                throw new InvalidOperationException($"Windows speech recognizer could not start: {compilation.Status}");
            }

            await _recognizer.ContinuousRecognitionSession.StartAsync();
            _isRunning = true;
        }
        catch
        {
            _recognizer.ContinuousRecognitionSession.ResultGenerated -= OnResultGenerated;
            _recognizer.Dispose();
            _recognizer = null;
            _isRunning = false;
            throw;
        }
    }

    public async Task<string> StopAsync()
    {
        if (!_isRunning || _recognizer is null) return FlushText();

        try
        {
            await _recognizer.ContinuousRecognitionSession.StopAsync();
        }
        finally
        {
            _isRunning = false;
            _recognizer.ContinuousRecognitionSession.ResultGenerated -= OnResultGenerated;
            _recognizer.Dispose();
            _recognizer = null;
        }

        return FlushText();
    }

    private void OnResultGenerated(SpeechContinuousRecognitionSession sender, SpeechContinuousRecognitionResultGeneratedEventArgs args)
    {
        var text = args.Result.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_buffer)
        {
            if (_buffer.Length > 0) _buffer.Append(' ');
            _buffer.Append(text);
        }
    }

    private string FlushText()
    {
        lock (_buffer)
        {
            var text = _buffer.ToString().Trim();
            _buffer.Clear();
            return text;
        }
    }

    private static SpeechRecognizer CreateRecognizer()
    {
        var preferredTags = new[]
        {
            CultureInfo.CurrentUICulture.Name,
            CultureInfo.CurrentCulture.Name,
            "zh-CN",
            "en-US",
        }
        .Where(tag => !string.IsNullOrWhiteSpace(tag))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

        var supported = SpeechRecognizer.SupportedTopicLanguages.ToList();
        foreach (var tag in preferredTags)
        {
            var language = supported.FirstOrDefault(l =>
                l.LanguageTag.Equals(tag, StringComparison.OrdinalIgnoreCase) ||
                l.LanguageTag.StartsWith(tag.Split('-')[0] + "-", StringComparison.OrdinalIgnoreCase));
            if (language is not null) return new SpeechRecognizer(language);
        }

        return new SpeechRecognizer(SpeechRecognizer.SystemSpeechLanguage ?? new Language("en-US"));
    }

    public void Dispose()
    {
        if (_recognizer is null) return;
        if (_isRunning)
        {
            try { _recognizer.ContinuousRecognitionSession.StopAsync().AsTask().GetAwaiter().GetResult(); }
            catch { }
        }
        _recognizer.ContinuousRecognitionSession.ResultGenerated -= OnResultGenerated;
        _recognizer.Dispose();
        _recognizer = null;
        _isRunning = false;
    }
}
