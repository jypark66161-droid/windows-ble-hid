using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

internal sealed class NativeInputCapture : IDisposable
{
    const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202;
    const int WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208;
    const int WM_MOUSEWHEEL = 0x20A, WM_QUIT = 0x12;
    const int VK_F8 = 0x77, VK_F9 = 0x78;
    const int LLKHF_INJECTED = 0x10, LLMHF_INJECTED = 0x01;
    readonly GattLocalCharacteristic mouse, keyboard;
    readonly Action<string> log;
    readonly Channel<InputEvent> queue = Channel.CreateBounded<InputEvent>(new BoundedChannelOptions(1024) {
        FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false
    });
    readonly HookProc keyboardDelegate, mouseDelegate;
    IntPtr keyboardHook, mouseHook;
    Thread? thread;
    uint threadId;
    int active;
    bool f8Down;
    int lastX, lastY;
    byte buttons;
    readonly HashSet<byte> held = new();
    byte modifiers;
    readonly CancellationTokenSource cancellation = new();
    readonly Task sender;
    readonly record struct InputEvent(int Kind, int A, int B);
    // Kind: 1 motion, 2 buttons, 3 wheel, 4 key, 5 reset.
    public bool Active => Volatile.Read(ref active) == 1;
    public bool Ready => mouse.SubscribedClients.Count > 0 && keyboard.SubscribedClients.Count > 0;
    public NativeInputCapture(GattLocalCharacteristic mouseReport, GattLocalCharacteristic keyboardReport, Action<string> logger)
    {
        mouse = mouseReport; keyboard = keyboardReport; log = logger;
        keyboardDelegate = KeyboardCallback; mouseDelegate = MouseCallback;
        sender = Task.Run(SendLoop);
        thread = new Thread(HookThread) { IsBackground = true, Name = "DeskBridge Native hooks" };
        thread.Start();
    }
    public void Toggle()
    {
        if (Active) { ReturnLocal("F8: PC input restored"); return; }
        if (!Ready) { log("F8: HID REPORT NOT SUBSCRIBED (mouse and keyboard required); input stays on PC"); return; }
        GetCursorPos(out POINT pos); lastX = pos.X; lastY = pos.Y;
        Volatile.Write(ref active, 1);
        log("CAPTURE ON: PC mouse and keyboard redirected to phone; F8 returns to PC, F9 emergency return");
    }
    public void ReturnLocal(string reason)
    {
        if (Interlocked.Exchange(ref active, 0) != 0)
        {
            queue.Writer.TryWrite(new InputEvent(5, 0, 0));
            log("CAPTURE OFF: " + reason);
        }
    }
    public void HealthCheck()
    {
        if (Active && !Ready) ReturnLocal("HID subscriber lost; automatic PC recovery");
        if (Active && (thread == null || !thread.IsAlive || keyboardHook == IntPtr.Zero || mouseHook == IntPtr.Zero))
            ReturnLocal("input hook unavailable; automatic PC recovery");
    }
    void HookThread()
    {
        threadId = GetCurrentThreadId();
        // Force creation of this thread's message queue before installing hooks.
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardDelegate, IntPtr.Zero, 0);
        mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseDelegate, IntPtr.Zero, 0);
        if (keyboardHook == IntPtr.Zero || mouseHook == IntPtr.Zero)
        {
            log($"INPUT HOOK FAILED: win32={Marshal.GetLastWin32Error()}");
            ReturnLocal("failed to install hooks");
            return;
        }
        log("INPUT HOOKS READY (F8 toggle / F9 emergency)");
        try { while (GetMessage(out MSG message, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); } }
        finally { ReturnLocal("hook thread stopped"); UnhookWindowsHookEx(keyboardHook); UnhookWindowsHookEx(mouseHook); }
    }
    IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        int msg = unchecked((int)wParam.ToInt64());
        bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
        bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
        if (!down && !up) return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        if ((info.flags & LLKHF_INJECTED) != 0) return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        if (info.vkCode == VK_F8)
        {
            if (down && !f8Down) { f8Down = true; Toggle(); }
            if (up) f8Down = false;
            return (IntPtr)1;
        }
        if (info.vkCode == VK_F9 && down) { ReturnLocal("F9 emergency return"); return (IntPtr)1; }
        if (!Active) return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        if (!queue.Writer.TryWrite(new InputEvent(4, unchecked((int)info.vkCode), down ? 1 : 0)))
        {
            ReturnLocal("input queue full, PC input restored");
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }
        return (IntPtr)1;
    }
    IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || !Active) return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        if ((info.flags & LLMHF_INJECTED) != 0) return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        int msg = unchecked((int)wParam.ToInt64());
        InputEvent? input = null;
        if (msg == WM_MOUSEMOVE)
        {
            int dx = info.pt.X - lastX, dy = info.pt.Y - lastY;
            lastX = info.pt.X; lastY = info.pt.Y;
            if (dx != 0 || dy != 0) input = new InputEvent(1, dx, dy);
        }
        else if (msg == WM_MOUSEWHEEL) input = new InputEvent(3, unchecked((short)(info.mouseData >> 16)), 0);
        else if (msg is WM_LBUTTONDOWN or WM_LBUTTONUP or WM_RBUTTONDOWN or WM_RBUTTONUP or WM_MBUTTONDOWN or WM_MBUTTONUP)
        {
            int bit = msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP ? 1 : msg == WM_RBUTTONDOWN || msg == WM_RBUTTONUP ? 2 : 4;
            bool press = msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN;
            input = new InputEvent(2, bit, press ? 1 : 0);
        }
        if (input.HasValue && !queue.Writer.TryWrite(input.Value))
        {
            ReturnLocal("mouse queue full, PC input restored");
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }
        return (IntPtr)1;
    }
    async Task SendLoop()
    {
        try
        {
            await foreach (var ev in queue.Reader.ReadAllAsync(cancellation.Token))
            {
                if (ev.Kind == 5) { await ReleaseAll(); continue; }
                if (!Active) continue;
                if (!Ready) { ReturnLocal("subscription missing while sending"); continue; }
                if (ev.Kind == 4)
                {
                    if (!TryMapKey(ev.A, out byte usage, out byte modifier)) continue;
                    if (modifier != 0) { if (ev.B == 1) modifiers |= modifier; else modifiers &= unchecked((byte)~modifier); }
                    else if (ev.B == 1) held.Add(usage); else held.Remove(usage);
                    var data = new byte[8]; data[0] = modifiers; int k = 2;
                    foreach (var key in held) { if (k >= data.Length) break; data[k++] = key; }
                    await Send(keyboard, data);
                }
                else if (ev.Kind == 1)
                {
                    int dx = ev.A, dy = ev.B;
                    while ((dx != 0 || dy != 0) && Active)
                    {
                        int x = Math.Clamp(dx, -127, 127), y = Math.Clamp(dy, -127, 127);
                        await Send(mouse, new byte[] { buttons, unchecked((byte)(sbyte)x), unchecked((byte)(sbyte)y), 0 });
                        dx -= x; dy -= y;
                    }
                }
                else if (ev.Kind == 2)
                {
                    if (ev.B == 1) buttons |= (byte)ev.A; else buttons &= unchecked((byte)~ev.A);
                    await Send(mouse, new byte[] { buttons, 0, 0, 0 });
                }
                else if (ev.Kind == 3)
                {
                    int steps = Math.Clamp(ev.A / 120, -127, 127);
                    if (steps != 0) await Send(mouse, new byte[] { buttons, 0, 0, unchecked((byte)(sbyte)steps) });
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReturnLocal("send worker exception: " + ex.Message); }
    }
    async Task Send(GattLocalCharacteristic characteristic, byte[] data)
    {
        try
        {
            var results = await characteristic.NotifyValueAsync(data.AsBuffer());
            if (results.Count == 0) ReturnLocal("notification had no recipients");
            else foreach (var r in results)
                if (r.Status != GattCommunicationStatus.Success) { ReturnLocal("notification failed: " + r.Status); break; }
        }
        catch (Exception ex) { ReturnLocal("BLE notification error: " + ex.Message); }
    }
    async Task ReleaseAll()
    {
        try
        {
            if (mouse.SubscribedClients.Count > 0) await mouse.NotifyValueAsync(new byte[4].AsBuffer());
            if (keyboard.SubscribedClients.Count > 0) await keyboard.NotifyValueAsync(new byte[8].AsBuffer());
        }
        catch { }
        buttons = 0; held.Clear(); modifiers = 0;
    }
    static bool TryMapKey(int vk, out byte usage, out byte modifier)
    {
        usage = 0; modifier = 0;
        if (vk >= 0x41 && vk <= 0x5A) { usage = (byte)(vk - 0x41 + 4); return true; }
        if (vk >= 0x31 && vk <= 0x39) { usage = (byte)(vk - 0x31 + 30); return true; }
        if (vk == 0x30) { usage = 39; return true; }
        if (vk is >= 0x70 and <= 0x76) { usage = (byte)(vk - 0x70 + 58); return true; }
        if (vk is >= 0x60 and <= 0x69) { usage = (byte)(vk - 0x60 + 98); return true; }
        if (vk is >= 0xA0 and <= 0xA5)
        {
            modifier = vk switch { 0xA0 => (byte)2, 0xA1 => (byte)32, 0xA2 => (byte)1, 0xA3 => (byte)16, 0xA4 => (byte)4, _ => (byte)64 };
            return true;
        }
        if (vk == 0x10) { modifier = 2; return true; }
        if (vk == 0x11) { modifier = 1; return true; }
        if (vk == 0x12) { modifier = 4; return true; }
        usage = vk switch {
            0x0D => 40, 0x1B => 41, 0x08 => 42, 0x09 => 43, 0x20 => 44,
            0xBD => 45, 0xBB => 46, 0xDB => 47, 0xDD => 48, 0xDC => 49,
            0xBA => 51, 0xDE => 52, 0xC0 => 53, 0xBC => 54, 0xBE => 55, 0xBF => 56,
            0x14 => 57, 0x2C => 70, 0x91 => 71, 0x13 => 72,
            0x2D => 73, 0x24 => 74, 0x21 => 75, 0x2E => 76,
            0x23 => 77, 0x22 => 78, 0x27 => 79, 0x25 => 80, 0x28 => 81, 0x26 => 82,
            0x6A => 85, 0x6B => 87, 0x6D => 86, 0x6E => 99, 0x6F => 84,
            _ => 0
        };
        return usage != 0;
    }
    public void Dispose()
    {
        ReturnLocal("application exiting");
        cancellation.Cancel();
        queue.Writer.TryComplete();
        if (threadId != 0) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public UIntPtr extraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public UIntPtr extraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; public uint lPrivate; }
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr hmod, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint threadId, int message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT point);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
}
