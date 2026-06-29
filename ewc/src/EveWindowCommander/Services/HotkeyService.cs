using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using EveWindowCommander.Models;

namespace EveWindowCommander.Services;

public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;

    // All valid bindings get a stable id up-front; OS registration is tracked separately so the
    // gated subset can be registered/unregistered as the foreground window changes.
    private readonly Dictionary<int, HotkeyBinding> _allById = new();
    private readonly HashSet<int> _registeredIds = new();
    private readonly List<int> _alwaysIds = new();
    private readonly List<int> _gatedIds = new();

    private HwndSource? _source;
    private nint _windowHandle;
    private int _nextId = 100;

    // Foreground-gating state.
    private bool _requireEveFocus;
    private Func<bool>? _isEveForeground;
    private LogService? _log;
    private bool _gatedActive;
    private nint _winEventHook;
    private WinEventDelegate? _winEventProc;   // kept alive to prevent GC of the callback

    public event EventHandler<HotkeyBinding>? HotkeyPressed;

    // FocusSlot and SwitchToCharacter are the primary ways to bring an EVE client to focus from
    // another app, so they're always registered even when EVE-focus gating is on.
    private static bool IsAlwaysOn(HotkeyBinding b) =>
        b.ActionId.StartsWith("FocusSlot", StringComparison.OrdinalIgnoreCase)
        || b.ActionId.StartsWith("SwitchToCharacter", StringComparison.OrdinalIgnoreCase);

    public void RegisterAll(nint windowHandle, IEnumerable<HotkeyBinding> bindings, LogService log,
        bool requireEveFocus, Func<bool> isEveForeground)
    {
        UnregisterAll(log);
        _windowHandle = windowHandle;
        _source = HwndSource.FromHwnd(windowHandle);
        _source?.AddHook(WndProc);
        _log = log;
        _requireEveFocus = requireEveFocus;
        _isEveForeground = isEveForeground;

        var failures = new List<string>();
        var seenGestures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in bindings.Where(b => b.Enabled && b.VirtualKey != 0))
        {
            try
            {
                SafetyGuard.ThrowIfInputBroadcastAction(binding.ActionId);
                var gestureKey = $"{binding.Modifiers}:{binding.VirtualKey}";
                if (!seenGestures.Add(gestureKey))
                {
                    failures.Add($"{binding.DisplayName} ({binding.GestureText}) duplicates another EWC hotkey.");
                    continue;
                }

                var id = _nextId++;
                _allById[id] = binding;
                (IsAlwaysOn(binding) ? _alwaysIds : _gatedIds).Add(id);
            }
            catch (Exception ex)
            {
                failures.Add($"{binding.DisplayName} ({binding.GestureText}): {ex.Message}");
            }
        }

        if (_requireEveFocus)
        {
            // Always-on hotkeys register immediately; gated ones follow the foreground window.
            foreach (var id in _alwaysIds) TryRegister(id, failures);
            InstallForegroundHook();
            UpdateGatedState();
        }
        else
        {
            foreach (var id in _allById.Keys) TryRegister(id, failures);
        }

        if (failures.Count > 0)
        {
            log.Error($"{failures.Count} hotkey(s) were not registered. Another app or another EWC instance may already own them. First conflict: {failures[0]}");
        }
    }

    public void UnregisterAll(LogService? log = null)
    {
        RemoveForegroundHook();

        foreach (var id in _registeredIds.ToList())
        {
            UnregisterHotKey(_windowHandle, id);
        }

        if (_registeredIds.Count > 0)
        {
            log?.Info($"Unregistered {_registeredIds.Count} hotkeys.");
        }

        _registeredIds.Clear();
        _allById.Clear();
        _alwaysIds.Clear();
        _gatedIds.Clear();
        _gatedActive = false;

        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }
    }

    public void Dispose() => UnregisterAll();

    private void TryRegister(int id, List<string> failures)
    {
        if (_registeredIds.Contains(id)) return;
        var binding = _allById[id];
        if (RegisterHotKey(_windowHandle, id, binding.Modifiers, binding.VirtualKey))
        {
            _registeredIds.Add(id);
            _log?.Info($"Registered hotkey {binding.GestureText} for {binding.DisplayName}.");
        }
        else
        {
            failures.Add($"{binding.DisplayName} ({binding.GestureText}): {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }
    }

    private void Unregister(int id)
    {
        if (_registeredIds.Remove(id)) UnregisterHotKey(_windowHandle, id);
    }

    // Registers/unregisters the gated subset to match whether an EVE client is foreground.
    private void UpdateGatedState()
    {
        if (!_requireEveFocus) return;
        var eveForeground = _isEveForeground?.Invoke() ?? false;
        if (eveForeground == _gatedActive) return;
        _gatedActive = eveForeground;

        if (eveForeground)
        {
            var failures = new List<string>();
            foreach (var id in _gatedIds) TryRegister(id, failures);
            if (failures.Count > 0) _log?.Error($"Could not register {failures.Count} EVE-focus hotkey(s): {failures[0]}");
        }
        else
        {
            foreach (var id in _gatedIds) Unregister(id);
        }
    }

    private void InstallForegroundHook()
    {
        if (_winEventHook != 0) return;
        _winEventProc = OnForegroundChanged;
        _winEventHook = SetWinEventHook(EventSystemForeground, EventSystemForeground,
            0, _winEventProc, 0, 0, WineventOutOfContext);
    }

    private void RemoveForegroundHook()
    {
        if (_winEventHook != 0)
        {
            UnhookWinEvent(_winEventHook);
            _winEventHook = 0;
        }
        _winEventProc = null;
    }

    private void OnForegroundChanged(nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        => UpdateGatedState();

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmHotkey && _allById.TryGetValue(wParam.ToInt32(), out var binding))
        {
            handled = true;
            HotkeyPressed?.Invoke(this, binding);
        }

        return 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    private delegate void WinEventDelegate(nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hWinEventHook);
}
