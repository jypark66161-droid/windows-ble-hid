using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

internal sealed class NativeInputCapture : IDisposable
{
    const int WH_KEYBOARD_LL=13, WH_MOUSE_LL=14;
    const uint WM_INPUT=0x00FF, WM_KEYDOWN=0x100, WM_KEYUP=0x101, WM_SYSKEYDOWN=0x104, WM_SYSKEYUP=0x105;
    const uint WM_MOUSEMOVE=0x200, WM_LBUTTONDOWN=0x201, WM_LBUTTONUP=0x202, WM_RBUTTONDOWN=0x204, WM_RBUTTONUP=0x205;
    const uint WM_MBUTTONDOWN=0x207, WM_MBUTTONUP=0x208, WM_MOUSEWHEEL=0x20A, WM_QUIT=0x12;
    const int VK_F8=0x77, VK_F9=0x78, RIM_TYPEMOUSE=0, RID_INPUT=0x10000003, RIDEV_INPUTSINK=0x100;
    readonly GattLocalCharacteristic mouse,keyboard,bootMouse,bootKeyboard;
    readonly Func<byte> protocolMode;
    readonly Action<string> log;
    readonly HookProc keyboardDelegate,mouseDelegate;
    readonly WndProc windowDelegate;
    readonly object stateLock=new();
    readonly Queue<KeyEvent> keys=new();
    readonly Queue<byte> buttonEvents=new();
    Thread? thread;
    IntPtr keyboardHook,mouseHook,window;
    uint threadId;
    int active, pending, generation;
    bool f8Down;
    int pendingDx,pendingDy,pendingWheel;
    byte buttons,modifiers;
    readonly HashSet<byte> held=new();
    readonly CancellationTokenSource cancellation=new();
    readonly Task sender;
    int rawPackets,mousePackets;
    readonly record struct KeyEvent(int Epoch,int Vk,bool Down);
    public bool Active => Volatile.Read(ref active)==1;
    public bool Ready => Select(mouse).SubscribedClients.Count>0 && Select(keyboard).SubscribedClients.Count>0;
    public NativeInputCapture(GattLocalCharacteristic m,GattLocalCharacteristic k,Action<string> logger,GattLocalCharacteristic bootM,GattLocalCharacteristic bootK,Func<byte> mode)
    {
        mouse=m; keyboard=k; bootMouse=bootM; bootKeyboard=bootK; protocolMode=mode; log=logger;
        keyboardDelegate=KeyboardCallback; mouseDelegate=MouseCallback; windowDelegate=WindowCallback;
        thread=new Thread(HookThread){IsBackground=true,Name="DeskBridge raw input"};
        thread.Start();
        sender=Task.Run(SendLoop);
    }
    public void Toggle()
    {
        if(Active) { ReturnLocal("F8"); return; }
        if(Interlocked.CompareExchange(ref pending,1,0)!=0) return;
        // Never block the Windows low-level hook thread on GATT.
        _=Task.Run(async()=>{
            try {
                if(!Ready || keyboardHook==IntPtr.Zero || mouseHook==IntPtr.Zero || window==IntPtr.Zero) { log("F8 REJECTED: HID subscription or input hooks missing; PC remains active"); return; }
                // A notification succeeding only confirms transport acceptance, not Android HID processing.
                var results=await Select(keyboard).NotifyValueAsync(new byte[8].AsBuffer()).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                if(results.Count==0 || results.Any(x=>x.Status!=GattCommunicationStatus.Success) || !Ready) {
                    log("F8 REJECTED: keyboard notification not accepted; PC remains active"); return;
                }
                lock(stateLock) {
                    keys.Clear();buttonEvents.Clear(); pendingDx=pendingDy=pendingWheel=0;
                    held.Clear(); buttons=modifiers=0;
                    Interlocked.Increment(ref generation);
                    Volatile.Write(ref active,1);
                }
                log("CAPTURE ON: PHONE (F8 PC return / F9 emergency)");
            }
            catch(Exception ex) { log("F8 REJECTED: "+ex.Message+"; PC remains active"); }
            finally { Volatile.Write(ref pending,0); }
        });
    }
    public void ReturnLocal(string reason)
    {
        lock(stateLock) {
            Interlocked.Increment(ref generation);
            keys.Clear();buttonEvents.Clear(); pendingDx=pendingDy=pendingWheel=0;
            if(Interlocked.Exchange(ref active,0)==1) log("CAPTURE OFF: PC, reason="+reason);
            // sender will release HID buttons/keys on its next cycle.
        }
    }
    public void HealthCheck()
    {
        if(Active && (!Ready || thread is null || !thread.IsAlive || keyboardHook==IntPtr.Zero || mouseHook==IntPtr.Zero || window==IntPtr.Zero))
            ReturnLocal("subscriber or hook lost");
    }
    void HookThread()
    {
        threadId=GetCurrentThreadId();
        PeekMessage(out _,IntPtr.Zero,0,0,0);
        string cls="DeskBridgeRawInput_"+Guid.NewGuid().ToString("N");
        var wc=new WNDCLASS { lpfnWndProc=windowDelegate, lpszClassName=cls };
        ushort atom=RegisterClass(ref wc);
        if(atom==0) { log("RAW INPUT: class registration failed "+Marshal.GetLastWin32Error()); return; }
        window=CreateWindowEx(0,cls,"DeskBridge Raw Input",0,0,0,0,0,new IntPtr(-3),IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        if(window==IntPtr.Zero) { log("RAW INPUT: window creation failed "+Marshal.GetLastWin32Error()); return; }
        var device=new RAWINPUTDEVICE { usUsagePage=1,usUsage=2,dwFlags=RIDEV_INPUTSINK,hwndTarget=window };
        if(!RegisterRawInputDevices(new[]{device},1,(uint)Marshal.SizeOf<RAWINPUTDEVICE>())) { log("RAW INPUT: register failed "+Marshal.GetLastWin32Error()); return; }
        keyboardHook=SetWindowsHookEx(WH_KEYBOARD_LL,keyboardDelegate,IntPtr.Zero,0);
        mouseHook=SetWindowsHookEx(WH_MOUSE_LL,mouseDelegate,IntPtr.Zero,0);
        if(keyboardHook==IntPtr.Zero || mouseHook==IntPtr.Zero) { log("HOOK FAILED "+Marshal.GetLastWin32Error()); return; }
        log("RAW INPUT READY; F8 PHONE/PC, F9 emergency PC");
        try { while(GetMessage(out MSG msg,IntPtr.Zero,0,0)>0) { TranslateMessage(ref msg);DispatchMessage(ref msg); } }
        finally {
            ReturnLocal("hook thread exit");
            if(keyboardHook!=IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
            if(mouseHook!=IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
            if(window!=IntPtr.Zero) DestroyWindow(window);
        }
    }
    IntPtr WindowCallback(IntPtr hWnd,uint msg,IntPtr wParam,IntPtr lParam)
    {
        if(msg==WM_INPUT && Active) {
            uint length=0;
            uint headerSize=(uint)Marshal.SizeOf<RAWINPUTHEADER>();
            if(GetRawInputData(lParam,RID_INPUT,IntPtr.Zero,ref length,headerSize)==0 && length>=headerSize+24 && length<4096) {
                IntPtr buffer=Marshal.AllocHGlobal((int)length);
                try {
                    if(GetRawInputData(lParam,RID_INPUT,buffer,ref length,headerSize)==length) {
                        var header=Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
                        if(header.dwType==RIM_TYPEMOUSE) {
                            int baseOffset=Marshal.SizeOf<RAWINPUTHEADER>();
                            ushort flags=(ushort)Marshal.ReadInt16(buffer,baseOffset);
                            if((flags&1)==0) { // RELATIVE; absolute packets must not move remote cursor
                                int dx=Marshal.ReadInt32(buffer,baseOffset+12),dy=Marshal.ReadInt32(buffer,baseOffset+16);
                                lock(stateLock) { if(Active) { pendingDx=(int)Math.Clamp((long)pendingDx+dx,-50000L,50000L); pendingDy=(int)Math.Clamp((long)pendingDy+dy,-50000L,50000L); } }
                                Interlocked.Increment(ref rawPackets);
                            }
                        }
                    }
                } finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        return DefWindowProc(hWnd,msg,wParam,lParam);
    }
    IntPtr KeyboardCallback(int code,IntPtr wp,IntPtr lp)
    {
        if(code<0) return CallNextHookEx(IntPtr.Zero,code,wp,lp);
        uint msg=(uint)wp.ToInt64();
        bool down=msg==WM_KEYDOWN||msg==WM_SYSKEYDOWN,up=msg==WM_KEYUP||msg==WM_SYSKEYUP;
        if(!down&&!up) return CallNextHookEx(IntPtr.Zero,code,wp,lp);
        var k=Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lp);
        if((k.flags&0x10)!=0) return CallNextHookEx(IntPtr.Zero,code,wp,lp);
        if(k.vkCode==VK_F8) {
            if(down && !f8Down) { f8Down=true; Toggle(); }
            if(up) f8Down=false;
            return (IntPtr)1;
        }
        if(k.vkCode==VK_F9 && down) { ReturnLocal("F9 emergency"); return (IntPtr)1; }
        if(!Active) return CallNextHookEx(IntPtr.Zero,code,wp,lp);
        lock(stateLock) {
            if(keys.Count>=128) { ReturnLocal("key backlog"); return CallNextHookEx(IntPtr.Zero,code,wp,lp); }
            keys.Enqueue(new KeyEvent(generation,(int)k.vkCode,down));
        }
        return (IntPtr)1;
    }
    IntPtr MouseCallback(int code,IntPtr wp,IntPtr lp)
    {
        if(code<0 || !Active) return CallNextHookEx(IntPtr.Zero,code,wp,lp);
        var m=Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lp);
        if((m.flags&1)!=0) return CallNextHookEx(IntPtr.Zero,code,wp,lp);
        uint msg=(uint)wp.ToInt64();
        if(msg==WM_MOUSEMOVE) { Interlocked.Increment(ref mousePackets); return (IntPtr)1; }
        lock(stateLock) {
            if(!Active) return CallNextHookEx(IntPtr.Zero,code,wp,lp);
            int bit=msg switch {WM_LBUTTONDOWN or WM_LBUTTONUP=>1,WM_RBUTTONDOWN or WM_RBUTTONUP=>2,WM_MBUTTONDOWN or WM_MBUTTONUP=>4,_=>0};
            if(bit!=0) { if(msg==WM_LBUTTONDOWN||msg==WM_RBUTTONDOWN||msg==WM_MBUTTONDOWN) buttons|=(byte)bit; else buttons&=(byte)~bit; if(buttonEvents.Count>=64)ReturnLocal("button backlog");else buttonEvents.Enqueue(buttons); }
            else if(msg==WM_MOUSEWHEEL) pendingWheel=Math.Clamp(pendingWheel+(short)(m.mouseData>>16)/120,-127,127);
            else return CallNextHookEx(IntPtr.Zero,code,wp,lp);
        }
        return (IntPtr)1;
    }
    async Task SendLoop()
    {
        int seenGeneration=-1;
        try {
            while(!cancellation.IsCancellationRequested) {
                await Task.Delay(12,cancellation.Token);
                int epoch,dx=0,dy=0,wheel=0;byte mouseButtons;KeyEvent[] batch;byte[] buttonBatch;
                bool on;
                lock(stateLock) {
                    epoch=generation;on=Active;
                    if(on) {
                        dx=Math.Clamp(pendingDx,-127,127); dy=Math.Clamp(pendingDy,-127,127);
                        pendingDx-=dx;pendingDy-=dy;
                        wheel=Math.Clamp(pendingWheel,-127,127);pendingWheel=0;
                        batch=keys.ToArray();keys.Clear();buttonBatch=buttonEvents.ToArray();buttonEvents.Clear();
                    } else {batch=Array.Empty<KeyEvent>();buttonBatch=Array.Empty<byte>();}
                    mouseButtons=buttons;
                }
                if(epoch!=seenGeneration) {
                    seenGeneration=epoch;
                    held.Clear();modifiers=0;buttons=0;
                    if(Ready) {
                        await Notify(keyboard,new byte[8],epoch);
                        await Notify(mouse,new byte[4],epoch);
                    }
                    continue;
                }
                if(!on) continue;
                if(!Ready) { ReturnLocal("HID report subscriptions lost"); continue; }
                foreach(var key in batch) {
                    if(key.Epoch!=generation || !Active) break;
                    if(!TryMapKey(key.Vk,out byte usage,out byte modifier)) continue;
                    if(modifier!=0) {if(key.Down)modifiers|=modifier;else modifiers&=(byte)~modifier;}
                    else {if(key.Down)held.Add(usage);else held.Remove(usage);}
                    byte[] bytes=new byte[8];bytes[0]=modifiers;int i=2;foreach(byte k in held) {if(i>=8)break;bytes[i++]=k;}
                    if(!await Notify(keyboard,bytes,epoch)) break;
                }
                if(!Active || epoch!=generation) continue;
                // At most one motion report per cycle, with bounded delta; stale motion never queues.
                foreach(byte edge in buttonBatch) { if(!Active || epoch!=generation) break; if(!await Notify(mouse,new byte[]{edge,0,0,0},epoch))break; }
                if(Active && epoch==generation && (dx!=0||dy!=0||wheel!=0))
                    await Notify(mouse,new byte[]{mouseButtons,unchecked((byte)(sbyte)dx),unchecked((byte)(sbyte)dy),unchecked((byte)(sbyte)wheel)},epoch);
            }
        } catch(OperationCanceledException) { }
        catch(Exception ex) { ReturnLocal("sender crashed: "+ex.Message); }
    }
    GattLocalCharacteristic Select(GattLocalCharacteristic report)
    {
        var boot=ReferenceEquals(report,mouse)?bootMouse:bootKeyboard;
        if(protocolMode()==0 && boot.SubscribedClients.Count>0) return boot;
        if(report.SubscribedClients.Count>0) return report;
        return boot;
    }
    async Task<bool> Notify(GattLocalCharacteristic characteristic,byte[] payload,int epoch)
    {
        try {
            var target=Select(characteristic);
            if(ReferenceEquals(target,bootMouse) && payload.Length==4) payload=payload[..3];
            var result=await target.NotifyValueAsync(payload.AsBuffer()).AsTask().WaitAsync(TimeSpan.FromSeconds(2),cancellation.Token);
            if(result.Count==0 || result.Any(x=>x.Status!=GattCommunicationStatus.Success)) {
                ReturnLocal("BLE notification rejected");return false;
            }
            return epoch==generation;
        } catch(OperationCanceledException) { return false; }
          catch(Exception ex) { ReturnLocal("BLE notify: "+ex.Message);return false; }
    }
    static bool TryMapKey(int vk,out byte usage,out byte mod)
    {
        usage=0;mod=0;
        if(vk>=0x41&&vk<=0x5A){usage=(byte)(vk-0x41+4);return true;}
        if(vk>=0x31&&vk<=0x39){usage=(byte)(vk-0x31+30);return true;}
        if(vk==0x30){usage=39;return true;}
        if(vk>=0xA0&&vk<=0xA5){mod=vk switch{0xA0=>2,0xA1=>32,0xA2=>1,0xA3=>16,0xA4=>4,_=>64};return true;}
        if(vk==0x10){mod=2;return true;} if(vk==0x11){mod=1;return true;} if(vk==0x12){mod=4;return true;}
        usage=vk switch{0x0D=>40,0x1B=>41,0x08=>42,0x09=>43,0x20=>44,0xBD=>45,0xBB=>46,0xDB=>47,0xDD=>48,0xDC=>49,0xBA=>51,0xDE=>52,0xC0=>53,0xBC=>54,0xBE=>55,0xBF=>56,0x24=>74,0x23=>77,0x25=>80,0x26=>82,0x27=>79,0x28=>81,0x2E=>76,_=>0};
        return usage!=0;
    }
    public void Dispose()
    {
        ReturnLocal("exit");cancellation.Cancel();
        if(threadId!=0)PostThreadMessage(threadId,WM_QUIT,IntPtr.Zero,IntPtr.Zero);
    }
    delegate IntPtr HookProc(int code,IntPtr wp,IntPtr lp);
    delegate IntPtr WndProc(IntPtr hwnd,uint msg,IntPtr wp,IntPtr lp);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct WNDCLASS {
        public uint style;public WndProc lpfnWndProc;public int cbClsExtra,cbWndExtra;
        public IntPtr hInstance,hIcon,hCursor,hbrBackground;public string? lpszMenuName,lpszClassName;
    }
    [StructLayout(LayoutKind.Sequential)]struct RAWINPUTDEVICE {public ushort usUsagePage,usUsage;public int dwFlags;public IntPtr hwndTarget;}
    [StructLayout(LayoutKind.Sequential)]struct RAWINPUTHEADER {public int dwType,dwSize;public IntPtr hDevice;public IntPtr wParam;}
    [StructLayout(LayoutKind.Sequential)]struct POINT {public int X,Y;}
    [StructLayout(LayoutKind.Sequential)]struct MSLLHOOKSTRUCT {public POINT pt;public uint mouseData,flags,time;public UIntPtr extraInfo;}
    [StructLayout(LayoutKind.Sequential)]struct KBDLLHOOKSTRUCT {public uint vkCode,scanCode,flags,time;public UIntPtr extraInfo;}
    [StructLayout(LayoutKind.Sequential)]struct MSG {public IntPtr hwnd;public uint message;public UIntPtr wParam;public IntPtr lParam;public uint time;public POINT pt;public uint lPrivate;}
    [DllImport("user32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern ushort RegisterClass(ref WNDCLASS cls);
    [DllImport("user32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern IntPtr CreateWindowEx(uint ex,string cls,string title,uint style,int x,int y,int w,int h,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr param);
    [DllImport("user32.dll")]static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")]static extern IntPtr DefWindowProc(IntPtr hwnd,uint msg,IntPtr wp,IntPtr lp);
    [DllImport("user32.dll",SetLastError=true)]static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] p,uint count,uint size);
    [DllImport("user32.dll",SetLastError=true)]static extern uint GetRawInputData(IntPtr raw,uint command,IntPtr data,ref uint size,uint headerSize);
    [DllImport("user32.dll",SetLastError=true)]static extern IntPtr SetWindowsHookEx(int id,HookProc callback,IntPtr hmod,uint thread);
    [DllImport("user32.dll")]static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr wp,IntPtr lp);
    [DllImport("user32.dll")]static extern int GetMessage(out MSG msg,IntPtr hwnd,uint min,uint max);
    [DllImport("user32.dll")]static extern bool PeekMessage(out MSG msg,IntPtr hwnd,uint min,uint max,uint remove);
    [DllImport("user32.dll")]static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")]static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")]static extern bool PostThreadMessage(uint id,uint msg,IntPtr wp,IntPtr lp);
    [DllImport("kernel32.dll")]static extern uint GetCurrentThreadId();
}
