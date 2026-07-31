using System.Diagnostics;
using System.Text.Json;

namespace Switchboard.Core.CustomActions;

/// <summary>
/// Runs one action block: a JSON object of the shape
/// <c>{ "name": "...", "steps": [ { "focus-window": "ms-teams" }, { "sleep": "160" }, ... ] }</c>.
/// Each step is a single-property object — the property name is the step kind,
/// its value is the one parameter — dispatched through a plain switch. This is
/// the format the eventual "Create New Action" builder targets: a user
/// assembles steps in the UI, no C# required.
///
/// If a step reports failure the rest of the block is skipped (used by
/// focus-window when the target app isn't running).
/// </summary>
public static class ActionStepRunner
{
    public static (string Name, bool RanFully) RunActionBlock(string blockJson)
    {
        using var doc = JsonDocument.Parse(blockJson);
        var root = doc.RootElement;
        var name = root.GetProperty("name").GetString() ?? "";
        foreach (var stepEl in root.GetProperty("steps").EnumerateArray())
        {
            var (step, value) = ReadStep(stepEl);
            if (!RunStep(step, value)) return (name, false);
        }
        return (name, true);
    }

    /// <summary>Whether a block contains a given step kind — used to decide if an
    /// action actually stole focus (via focus-window) and therefore has anything
    /// to hand back afterward. Actions that only hold/release/send keys never
    /// take focus away from wherever the user already is.</summary>
    public static bool BlockHasStep(string blockJson, string stepKind)
    {
        using var doc = JsonDocument.Parse(blockJson);
        return doc.RootElement.GetProperty("steps").EnumerateArray()
            .Any(stepEl => stepEl.EnumerateObject().First().Name == stepKind);
    }

    /// <summary>Human-readable, numbered rendering of a block — feeds the
    /// assignment wizard's "what this does" summary.</summary>
    public static string DescribeActionBlock(string blockJson)
    {
        using var doc = JsonDocument.Parse(blockJson);
        var root = doc.RootElement;
        var name = root.GetProperty("name").GetString() ?? "";
        var lines = new List<string> { $"{name}:" };
        foreach (var stepEl in root.GetProperty("steps").EnumerateArray())
        {
            var (step, value) = ReadStep(stepEl);
            lines.Add("   - " + DescribeStep(step, value));
        }
        return string.Join("\n", lines);
    }

    private static (string Step, string Value) ReadStep(JsonElement stepEl)
    {
        var prop = stepEl.EnumerateObject().First();
        return (prop.Name, prop.Value.GetString() ?? "");
    }

    private static bool RunStep(string step, string value) => step switch
    {
        "focus-window" => ActionStepFocusWindow(value),
        "sleep" => ActionStepSleep(value),
        "send-keys" => ActionStepSendKeys(value),
        "clear-field" => ActionStepClearField(),
        "hold-key" => ActionStepHoldKey(value),
        "release-key" => ActionStepReleaseKey(value),
        "run" => ActionStepRun(value),
        "window" => WindowActions.Apply(value),
        "run-action" => ActionStepRunAction(value),
        _ => true,
    };

    /// <summary>Plain-English rendering of one step — shared by the wizard's "what
    /// this does" summary and the action builder's live preview, so both read the
    /// same way.</summary>
    public static string DescribeStep(string step, string value) => step switch
    {
        "focus-window" => DescribeFocusWindow(value),
        "sleep" => $"Sleep for {value}ms.",
        "send-keys" => $"Send {DescribeSendKeysValue(value)}.",
        "clear-field" => "Clear the field (select all, then delete).",
        "hold-key" => DescribeHoldKey(value),
        "release-key" => $"Release {value} (does nothing if it isn't currently held).",
        "run" => $"Run: {value}",
        "window" => DescribeWindow(value),
        "run-action" => $"Run the action \"{value}\".",
        _ => $"Unknown step \"{step}\".",
    };

    private static string DescribeHoldKey(string value)
    {
        var (name, timeoutMs) = ParseHoldSpec(value);
        return $"Hold {name} down (auto-releases after {timeoutMs}ms unless another hold-key step for {name} runs first).";
    }

    private static string DescribeSendKeysValue(string value) =>
        TryResolveKeySpec(value, out _, out _, out _, out _, out _) ? $"the key {value}" : $"the text \"{value}\"";

    private static string DescribeFocusWindow(string value)
    {
        var parts = value.Split('|', 2);
        return parts.Length < 2
            ? $"Focus the {parts[0]} window (skips the rest of this block if it isn't running)."
            : $"Focus the {parts[0]} window, launching it via \"{parts[1]}\" first if it isn't already running.";
    }

    private static string DescribeWindow(string value)
    {
        var parts = value.Split(':', 2);
        var verb = parts[0].Trim();
        var arg = parts.Length > 1 ? parts[1].Trim() : "";
        return verb switch
        {
            "pin" => "Pin the active window on top of every other window.",
            "unpin" => "Unpin the active window (no longer always-on-top).",
            "toggle-topmost" => "Toggle whether the active window stays on top of every other window.",
            "maximize" => "Maximize the active window.",
            "minimize" => "Minimize the active window.",
            "restore" => "Restore the active window to its normal size.",
            "close" => "Close the active window.",
            "opacity" => $"Set the active window's opacity to {arg}%.",
            "monitor" => $"Move the active window to the {arg} monitor.",
            _ => $"Unknown window command \"{value}\".",
        };
    }

    // ---- Step kinds ----

    /// <summary>Focuses a running app by process name; value can add "|launch-command"
    /// so it launches (and then focuses) the app if it isn't already running, instead
    /// of just skipping the rest of the block.</summary>
    private static bool ActionStepFocusWindow(string value)
    {
        var parts = value.Split('|', 2);
        var processName = parts[0];
        var window = FindMainWindow(processName);
        if (window != IntPtr.Zero) return ForegroundStealer.Focus(window);
        if (parts.Length < 2 || !ActionStepRun(parts[1])) return false;

        for (var i = 0; i < 20; i++)
        {
            Thread.Sleep(150);
            window = FindMainWindow(processName);
            if (window != IntPtr.Zero) return ForegroundStealer.Focus(window);
        }
        return true; // launched, but couldn't confirm/focus a window within ~3s
    }

    private static IntPtr FindMainWindow(string processName) =>
        Process.GetProcessesByName(processName).Select(p => p.MainWindowHandle).FirstOrDefault(h => h != IntPtr.Zero);

    /// <summary>Runs any command line via the shell — an exe, a script, a file, or a
    /// URL — exactly as if typed into Run or a terminal. The generic escape hatch
    /// for anything that doesn't need its own step kind.</summary>
    private static bool ActionStepRun(string commandLine)
    {
        try
        {
            var (fileName, arguments) = SplitCommandLine(commandLine);
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (string FileName, string Arguments) SplitCommandLine(string commandLine)
    {
        var s = commandLine.Trim();
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            if (end > 0) return (s[1..end], s[(end + 1)..].TrimStart());
        }
        var space = s.IndexOf(' ');
        return space < 0 ? (s, "") : (s[..space], s[(space + 1)..]);
    }

    // A run-action step invoking another action that (directly or transitively)
    // run-actions back is a mistake, not a valid use case — cap the depth instead
    // of letting it recurse until the stack blows.
    [ThreadStatic] private static int _runActionDepth;
    private const int MaxRunActionDepth = 5;

    private static bool ActionStepRunAction(string actionId)
    {
        if (_runActionDepth >= MaxRunActionDepth || ActionCatalog.DefaultHost is not { } host) return false;
        _runActionDepth++;
        try { ActionCatalog.Run(actionId, host); }
        finally { _runActionDepth--; }
        return true;
    }

    private static bool ActionStepSleep(string milliseconds)
    {
        if (int.TryParse(milliseconds, out var ms)) Thread.Sleep(ms);
        return true;
    }

    /// <summary>Sends a named key/chord (e.g. "Ctrl+E", "Enter", "Esc") if the value
    /// resolves to one, else types it as literal text (e.g. "/away"). One step
    /// covers both — a keypress and typed text are the same operation to the user.</summary>
    private static bool ActionStepSendKeys(string value)
    {
        if (TryResolveKeySpec(value, out var vk, out var ctrl, out var shift, out var alt, out var win))
            KeystrokeSender.PressChord(vk, ctrl, shift, alt, win);
        else
            KeystrokeSender.TypeText(value);
        return true;
    }

    private static bool ActionStepClearField()
    {
        KeystrokeSender.PressChord(KeystrokeSender.VK_A, ctrl: true);
        KeystrokeSender.PressKey(KeystrokeSender.VK_DELETE);
        return true;
    }

    /// <summary>Holds a modifier down without releasing it — the building block
    /// for multi-trigger sessions (e.g. one action per knob-turn direction
    /// holding Alt while a separate "release-key" action, bound to the press,
    /// lets go of it). Value is "Name" or "Name:timeoutMs", e.g. "Alt:5000".
    /// The timeout always applies — this can never hold a key forever.</summary>
    private static bool ActionStepHoldKey(string value)
    {
        var (name, timeoutMs) = ParseHoldSpec(value);
        if (TryResolveModifierVk(name, out var vk)) HeldKeyRegistry.Hold(vk, TimeSpan.FromMilliseconds(timeoutMs));
        return true;
    }

    private static bool ActionStepReleaseKey(string value)
    {
        if (TryResolveModifierVk(value.Trim(), out var vk)) HeldKeyRegistry.Release(vk);
        return true;
    }

    private const int DefaultHoldTimeoutMs = 5000;

    private static (string Name, int TimeoutMs) ParseHoldSpec(string value)
    {
        var parts = value.Split(':', 2);
        var name = parts[0].Trim();
        var timeoutMs = parts.Length > 1 && int.TryParse(parts[1], out var ms) ? ms : DefaultHoldTimeoutMs;
        return (name, timeoutMs);
    }

    private static bool TryResolveModifierVk(string name, out ushort vk)
    {
        vk = name switch
        {
            "Ctrl" => KeystrokeSender.VK_CONTROL,
            "Shift" => KeystrokeSender.VK_SHIFT,
            "Alt" => KeystrokeSender.VK_ALT,
            "Win" => KeystrokeSender.VK_LWIN,
            _ => (ushort)0,
        };
        return vk != 0;
    }

    private static bool TryResolveKeySpec(string value, out ushort vk, out bool ctrl, out bool shift, out bool alt, out bool win)
    {
        vk = 0;
        ctrl = shift = alt = win = false;
        var parts = value.Split('+');
        if (MegalodonPad.BasicCodeFromName(parts[^1]) is not byte basicCode) return false;
        if (KeycodeCatalog.BasicToVk(basicCode) is not ushort mappedVk) return false;
        vk = mappedVk;
        foreach (var mod in parts[..^1])
        {
            if (mod == "Ctrl") ctrl = true;
            else if (mod == "Shift") shift = true;
            else if (mod == "Alt") alt = true;
            else if (mod == "Win") win = true;
        }
        return true;
    }
}
