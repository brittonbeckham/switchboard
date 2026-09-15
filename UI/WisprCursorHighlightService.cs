using System.Runtime.InteropServices;
using Switchboard.Core;
using Switchboard.Core.CustomActions;
using Switchboard.Util;

namespace Switchboard.UI;

/// <summary>
/// Follows Wispr Flow's dictation flag and shows the caret ring. With Auto submit
/// on, a normal finish waits until paste has landed in the caret field, then
/// presses Enter. Cancel (no paste) times out without submitting.
/// </summary>
internal sealed class WisprCursorHighlightService : IDisposable
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Wispr Flow", "config.json");

    private const int OverallWaitMs = 15_000;
    private const int StableClipboardMs = 120;
    private const int StableCaretMs = 160;
    private const int StableFieldMs = 100;
    private const ushort VkEscape = 0x1B;
    private const ushort VkEnter = 0x0D;
    private const ushort VkControl = 0x11;
    private const ushort VkV = 0x56;

    private readonly AppSettings _settings;
    private readonly WisprCursorRing _ring = new();
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 200 };
    private readonly SynchronizationContext? _ui;
    private bool _dictating;
    private bool _loggedMissing;
    private bool _disposed;
    private string? _clipboardBaseline;
    private CancellationTokenSource? _submitCts;
    private int _submitGeneration;

    public WisprCursorHighlightService(AppSettings settings)
    {
        _settings = settings;
        _ui = SynchronizationContext.Current;
        _ring.AutoSubmit = settings.WisprAutoSubmitEnabled;
        _ring.AutoSubmitChanged += OnAutoSubmitChanged;
        _poll.Tick += (_, _) => Poll();
        _poll.Start();
        Log.Info("Wispr cursor highlight: following Wispr Flow dictation state.");
        Poll();
    }

    /// <summary>Pull Auto submit from settings (Extras toggle) onto the live ring.</summary>
    public void ApplyAutoSubmitSetting() => _ring.AutoSubmit = _settings.WisprAutoSubmitEnabled;

    private void OnAutoSubmitChanged(bool enabled)
    {
        _settings.WisprAutoSubmitEnabled = enabled;
        _settings.Save();
    }

    private void Poll()
    {
        if (_disposed) return;

        bool active;
        try
        {
            if (!File.Exists(ConfigPath))
            {
                if (!_loggedMissing)
                {
                    Log.Info("Wispr cursor highlight: Wispr Flow config not found yet.");
                    _loggedMissing = true;
                }
                active = false;
            }
            else
            {
                _loggedMissing = false;
                var text = File.ReadAllText(ConfigPath);
                active = text.Contains("\"activeDictationSession\": {", StringComparison.Ordinal)
                      || text.Contains("\"activeDictationSession\":{", StringComparison.Ordinal);
            }
        }
        catch
        {
            return;
        }

        if (active == _dictating) return;

        if (active)
        {
            _submitCts?.Cancel();
            // Drain Escape edge bit so a prior Esc doesn't poison this session.
            _ = GetAsyncKeyState(VkEscape);
            _clipboardBaseline = ReadClipboardText();
            _dictating = true;
            _ring.SetActive(true);
            return;
        }

        _dictating = false;
        var autoSubmit = _ring.AutoSubmit;
        var baseline = _clipboardBaseline;
        // Escape while the session is ending → treat as cancel (no auto-submit).
        var cancelled = (GetAsyncKeyState(VkEscape) & 0x8000) != 0
                        || (GetAsyncKeyState(VkEscape) & 0x0001) != 0;
        _ring.SetActive(false);

        if (!autoSubmit || cancelled)
        {
            if (cancelled && autoSubmit)
                Log.Info("Wispr auto-submit: skipped (Escape).");
            return;
        }

        StartAutoSubmit(baseline);
    }

    private void StartAutoSubmit(string? clipboardBaseline)
    {
        _submitCts?.Cancel();
        _submitCts?.Dispose();
        _submitCts = new CancellationTokenSource();
        var cts = _submitCts;
        var gen = Interlocked.Increment(ref _submitGeneration);
        var token = cts.Token;
        Log.Info("Wispr auto-submit: waiting for paste…");

        Task.Run(async () =>
        {
            try
            {
                var ok = await WaitUntilPasteLandedAsync(clipboardBaseline, token).ConfigureAwait(false);
                if (!ok || token.IsCancellationRequested || gen != _submitGeneration) return;

                // Don't fire Enter into Wispr's own UI — wait until the target app is front.
                var focusDeadline = Environment.TickCount64 + 2000;
                while (Environment.TickCount64 < focusDeadline)
                {
                    token.ThrowIfCancellationRequested();
                    if (!TextCaretLocator.ForegroundIsWispr()) break;
                    await Task.Delay(40, token).ConfigureAwait(false);
                }

                RunOnUi(() =>
                {
                    if (_disposed || gen != _submitGeneration) return;
                    if (TextCaretLocator.ForegroundIsWispr())
                    {
                        Log.Info("Wispr auto-submit: target app not focused — Enter skipped.");
                        return;
                    }
                    KeystrokeSender.PressKey(VkEnter);
                    Log.Info("Wispr auto-submit: Enter sent.");
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Info($"Wispr auto-submit: aborted ({ex.GetType().Name}).");
            }
        }, token);
    }

    /// <summary>
    /// Confirm paste without assuming which field you started in:
    /// 1) clipboard gets new text (Wispr prepared paste), and/or Ctrl+V,
    /// 2) caret under the ring moves and settles, or that field's text gains the paste.
    /// Cancel with no paste → timeout, no Enter.
    /// </summary>
    private async Task<bool> WaitUntilPasteLandedAsync(string? clipboardBaseline, CancellationToken token)
    {
        var deadline = Environment.TickCount64 + OverallWaitMs;
        string? expected = null;
        long expectedAt = 0;
        string? lastClip = null;
        long clipStableSince = 0;

        // Capture caret/field only once clipboard (or paste chord) says paste is coming —
        // that is the real target after bounce-around.
        int preX = 0, preY = 0;
        var havePreCaret = false;
        string? preField = null;
        var armed = false;

        int settleX = 0, settleY = 0;
        long caretMovedAt = 0;
        long caretStableSince = 0;
        string? lastField = null;
        long fieldMatchSince = 0;

        var sawPasteChord = false;
        var pasteChordWasDown = false;

        while (Environment.TickCount64 < deadline)
        {
            token.ThrowIfCancellationRequested();

            var ctrl = (GetAsyncKeyState(VkControl) & 0x8000) != 0;
            var vDown = (GetAsyncKeyState(VkV) & 0x8000) != 0;
            if (ctrl && vDown) pasteChordWasDown = true;
            if (pasteChordWasDown && !vDown)
            {
                sawPasteChord = true;
                pasteChordWasDown = false;
            }

            var clip = await ReadClipboardOnUiAsync(token).ConfigureAwait(false);
            if (expected == null
                && clip != null
                && clip.Length > 0
                && !string.Equals(clip, clipboardBaseline, StringComparison.Ordinal))
            {
                if (clip == lastClip)
                {
                    if (Environment.TickCount64 - clipStableSince >= StableClipboardMs)
                    {
                        expected = clip;
                        expectedAt = Environment.TickCount64;
                        Log.Info($"Wispr auto-submit: clipboard ready ({clip.Length} chars).");
                    }
                }
                else
                {
                    lastClip = clip;
                    clipStableSince = Environment.TickCount64;
                }
            }

            // Arm when clipboard is ready or Ctrl+V completed — then snapshot the caret target.
            if (!armed && (expected != null || sawPasteChord))
            {
                armed = true;
                if (TextCaretLocator.TryGet(out preX, out preY, out _, out _))
                {
                    havePreCaret = true;
                    settleX = preX;
                    settleY = preY;
                }
                preField = await ReadFieldOnUiAsync(token).ConfigureAwait(false);
                Log.Info($"Wispr auto-submit: armed (caret={(havePreCaret ? $"{preX},{preY}" : "none")}, fieldLen={preField?.Length ?? -1}).");
            }

            if (!armed)
            {
                await Task.Delay(40, token).ConfigureAwait(false);
                continue;
            }

            // Primary: text under the caret gained the clipboard paste.
            var field = await ReadFieldOnUiAsync(token).ConfigureAwait(false);
            if (expected != null && field != null
                && TextCaretLocator.PasteAppearedInField(preField, field, expected))
            {
                if (field == lastField)
                {
                    if (Environment.TickCount64 - fieldMatchSince >= StableFieldMs)
                    {
                        Log.Info("Wispr auto-submit: confirmed via field text.");
                        return true;
                    }
                }
                else
                {
                    lastField = field;
                    fieldMatchSince = Environment.TickCount64;
                }
            }
            else if (field != null && preField != null
                     && !string.Equals(field, preField, StringComparison.Ordinal)
                     && field.Length > preField.Length
                     && (sawPasteChord || expected != null))
            {
                if (field == lastField)
                {
                    if (Environment.TickCount64 - fieldMatchSince >= StableFieldMs)
                    {
                        Log.Info("Wispr auto-submit: confirmed via field growth.");
                        return true;
                    }
                }
                else
                {
                    lastField = field;
                    fieldMatchSince = Environment.TickCount64;
                }
            }

            // Secondary: caret advanced after arming (paste moves the insertion point).
            if (havePreCaret && TextCaretLocator.TryGet(out var cx, out var cy, out _, out _))
            {
                var moved = Math.Abs(cx - preX) >= 2 || Math.Abs(cy - preY) >= 2;
                if (moved)
                {
                    if (cx != settleX || cy != settleY)
                    {
                        settleX = cx;
                        settleY = cy;
                        caretMovedAt = Environment.TickCount64;
                        caretStableSince = Environment.TickCount64;
                    }
                    else if (caretMovedAt > 0
                             && Environment.TickCount64 - caretStableSince >= StableCaretMs
                             && (sawPasteChord || expected != null
                                 || Environment.TickCount64 - expectedAt >= 50))
                    {
                        Log.Info("Wispr auto-submit: confirmed via caret move.");
                        return true;
                    }
                }
            }

            await Task.Delay(40, token).ConfigureAwait(false);
        }

        Log.Info("Wispr auto-submit: timed out — Enter skipped.");
        return false;
    }

    private string? ReadClipboardText()
    {
        try
        {
            if (!Clipboard.ContainsText()) return "";
            return Clipboard.GetText() ?? "";
        }
        catch
        {
            return null;
        }
    }

    private Task<string?> ReadClipboardOnUiAsync(CancellationToken token)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUi(() =>
        {
            if (token.IsCancellationRequested) { tcs.TrySetCanceled(token); return; }
            tcs.TrySetResult(ReadClipboardText());
        });
        return tcs.Task;
    }

    private Task<string?> ReadFieldOnUiAsync(CancellationToken token)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUi(() =>
        {
            if (token.IsCancellationRequested) { tcs.TrySetCanceled(token); return; }
            try
            {
                tcs.TrySetResult(TextCaretLocator.TryReadFocusedText(out var t) ? t : null);
            }
            catch
            {
                tcs.TrySetResult(null);
            }
        });
        return tcs.Task;
    }

    private void RunOnUi(Action action)
    {
        if (_ui != null)
            _ui.Post(_ => action(), null);
        else
            action();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _submitCts?.Cancel();
        _submitCts?.Dispose();
        _poll.Stop();
        _poll.Dispose();
        _ring.AutoSubmitChanged -= OnAutoSubmitChanged;
        _ring.SetActive(false);
        _ring.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
