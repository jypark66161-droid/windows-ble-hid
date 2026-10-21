using System.Runtime.InteropServices.WindowsRuntime;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

// Mouse movement ONLY: keyboard hook handles local F8/F9; keyboard is NEVER sent via BLE.
// Mouse buttons/wheel are not sent either. All reports are [buttons=0, dx, dy, wheel=0].
internal sealed class MouseOnlyCapture : IDisposable
{
    const int WH_KEYBOARD_LL=13, WH_MOUSE_LL=14;
    const uint WM_INPUT=0x00FF, WM_KEYDOWN=0x100, WM_KEYUP=0x101, WM_SYSKEYDOWN=0x104, WM_SYSKEYUP=0x105;
    const uint WM_MOUSEMOVE=0x200, WM_LBUTTONDOWN=0x201, WM_LBUTTONUP=0x202, WM_RBUTTONDOWN=0x204, WM_RBUTTONUP=0x205;
    const uint WM_MBUTTONDOWN=0x207, WM_MBUTTONUP=0x208, WM_MOUSEWHEEL=0x20A, WM_QUIT=0x12;
    const int VK_F8=0x77,VK_F9=0x78,RID_INPUT=0x10000003,RIDEV_INPUTSINK=0x100,RIM_TYPEMOUSE=0;
    readonly GattLocalCharacteristic mouse;
    readonly Action<string> log;
    readonly HookProc keyboardCallback,mouseCallback;
    readonly WndProc windowCallback;
    readonly CancellationTokenSource cancellation=new();
    readonly SemaphoreSlim notifyGate=new(1,1);
    readonly object movementLock=new();
    readonly Thread hookThread;
    readonly Task sender;
    IntPtr hwnd,keyboardHook,mouseHook;
    uint hookThreadId;
    int active,pending,epoch;
    int dxPending,dyPending;
    bool f8Down;
    public bool Active => Volatile.Read(ref active)==1;
    public MouseOnlyCapture(GattLocalCharacteristic mouse,Action<string> log)
    {
        this.mouse=mouse;this.log=log;
        keyboardCallback=KeyboardHook;mouseCallback=MouseHook;windowCallback=WindowProc;
        hookThread=new Thread(RunHooks){IsBackground=true,Name="DeskBridge MOUSE ONLY raw input"};
        hookThread.Start();
        sender=Task.Run(SendLoop);
    }
    public void ReturnPc(string reason)
    {
        lock(movementLock) {
            Interlocked.Increment(ref epoch);
            dxPending=dyPending=0;
            if(Interlocked.Exchange(ref active,0)==1) log("F8 CAPTURE OFF / PC: "+reason);
        }
    }
    void Toggle()
    {
        if(Active) {ReturnPc("F8");return;}
        if(Interlocked.CompareExchange(ref pending,1,0)!=0)return;
        _=Task.Run(async()=>{
            try {
                if(mouse.SubscribedClients.Count==0 || keyboardHook==IntPtr.Zero ||
                    mouseHook==IntPtr.Zero || hwnd==IntPtr.Zero) {
                    log("F8 REJECTED: no HID mouse subscriber or Windows input hook");return;
                }
                // One neutral movement-only report checks that Windows accepts notifications.
                if(!await notifyGate.WaitAsync(TimeSpan.FromSeconds(2))) {
                    log("F8 REJECTED: BLE sender is busy");return;
                }
                bool ok=false;
                try {
                    var operation=mouse.NotifyValueAsync(new byte[4].AsBuffer());
                    try {
                        var results=await operation.AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                        ok=results.Count>0 && results.All(x=>x.Status==GattCommunicationStatus.Success);
                    }catch(TimeoutException){operation.Cancel();}
                }finally{notifyGate.Release();}
                if(!ok || mouse.SubscribedClients.Count==0) {
                    log("F8 REJECTED: mouse notification unavailable; PC remains active");return;
                }
                lock(movementLock) {
                    dxPending=dyPending=0;
                    Interlocked.Increment(ref epoch);
                    Volatile.Write(ref active,1);
                }
                log("F8 CAPTURE ON / PHONE: relative MOUSE MOVEMENT ONLY");
            }catch(Exception ex){log("F8 REJECTED: "+ex.Message);}
            finally{Volatile.Write(ref pending,0);}
        });
    }
    void RunHooks()
    {
        hookThreadId=GetCurrentThreadId();
        PeekMessage(out _,IntPtr.Zero,0,0,0);
        string cls="DeskBridgeMouseOnly_"+Guid.NewGuid().ToString("N");
        var wc=new WNDCLASS{lpfnWndProc=windowCallback,lpszClassName=cls};
        if(RegisterClass(ref wc)==0){log("RAWINPUT RegisterClass failed "+Marshal.GetLastWin32Error());return;}
        hwnd=CreateWindowEx(0,cls,"DeskBridge Mouse Only",0,0,0,0,0,
            new IntPtr(-3),IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        if(hwnd==IntPtr.Zero){log("RAWINPUT CreateWindowEx failed "+Marshal.GetLastWin32Error());return;}
        var dev=new RAWINPUTDEVICE{usUsagePage=1,usUsage=2,dwFlags=RIDEV_INPUTSINK,hwndTarget=hwnd};
        if(!RegisterRawInputDevices(new[]{dev},1,(uint)Marshal.SizeOf<RAWINPUTDEVICE>())){
            log("RAWINPUT registration failed "+Marshal.GetLastWin32Error());return;
        }
        keyboardHook=SetWindowsHookEx(WH_KEYBOARD_LL,keyboardCallback,IntPtr.Zero,0);
        mouseHook=SetWindowsHookEx(WH_MOUSE_LL,mouseCallback,IntPtr.Zero,0);
        if(keyboardHook==IntPtr.Zero || mouseHook==IntPtr.Zero) {
            log("INPUT HOOK registration failed "+Marshal.GetLastWin32Error());return;
        }
        log("RAW INPUT READY / keyboard locally monitors only F8 and F9; NO keyboard BLE report");
        try {
            while(GetMessage(out MSG msg,IntPtr.Zero,0,0)>0){
                TranslateMessage(ref msg);DispatchMessage(ref msg);
            }
        }finally {
            ReturnPc("hook thread exited");
            if(keyboardHook!=IntPtr.Zero)UnhookWindowsHookEx(keyboardHook);
            if(mouseHook!=IntPtr.Zero)UnhookWindowsHookEx(mouseHook);
            if(hwnd!=IntPtr.Zero)DestroyWindow(hwnd);
        }
    }
    IntPtr KeyboardHook(int code,IntPtr wp,IntPtr lp)
    {
        if(code<0)return CallNextHookEx(IntPtr.Zero,code,wp,lp);
        uint msg=(uint)wp.ToInt64();
        bool down=msg==WM_KEYDOWN || msg==WM_SYSKEYDOWN;
        bool up=msg==WM_KEYUP || msg==WM_SYSKEYUP;
        if(!down && !up)return CallNextHookEx(IntPtr.Zero,code,wp,lp);
        var k=Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lp);
        if((k.flags&0x10)!=0)return CallNextHookEx(IntPtr.Zero,code,wp,lp);
        if(k.vkCode==VK_F8) {
            if(down && !f8Down){f8Down=true;Toggle();}
            if(up)f8Down=false;
            return (IntPtr)1;
        }
        if(k.vkCode==VK_F9 && down){ReturnPc("F9 emergency");return (IntPtr)1;}
        // Never capture or transmit other keys.
        return CallNextHookEx(IntPtr.Zero,code,wp,lp);
    }
    IntPtr MouseHook(int code,IntPtr wp,IntPtr lp)
    {
        if(code<0 || !Active)return CallNextHookEx(IntPtr.Zero,code,wp,lp);
        var m=Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lp);
        if((m.flags&1)!=0)return CallNextHookEx(IntPtr.Zero,code,wp,lp);
        uint msg=(uint)wp.ToInt64();
        if(msg==WM_MOUSEMOVE || msg==WM_LBUTTONDOWN || msg==WM_LBUTTONUP ||
           msg==WM_RBUTTONDOWN || msg==WM_RBUTTONUP ||
           msg==WM_MBUTTONDOWN || msg==WM_MBUTTONUP || msg==WM_MOUSEWHEEL)
            return (IntPtr)1; // block local mouse interaction; never transmit clicks/wheel
        return CallNextHookEx(IntPtr.Zero,code,wp,lp);
    }
    IntPtr WindowProc(IntPtr hWnd,uint msg,IntPtr wp,IntPtr lp)
    {
        if(msg==WM_INPUT && Active){
            uint length=0;
            uint headerSize=(uint)Marshal.SizeOf<RAWINPUTHEADER>();
            if(GetRawInputData(lp,RID_INPUT,IntPtr.Zero,ref length,headerSize)==0 &&
               length>=headerSize+24 && length<4096){
                var ptr=Marshal.AllocHGlobal((int)length);
                try{
                    if(GetRawInputData(lp,RID_INPUT,ptr,ref length,headerSize)==length){
                        var hdr=Marshal.PtrToStructure<RAWINPUTHEADER>(ptr);
                        if(hdr.dwType==RIM_TYPEMOUSE){
                            int offset=Marshal.SizeOf<RAWINPUTHEADER>();
                            ushort flags=(ushort)Marshal.ReadInt16(ptr,offset);
                            if((flags&1)==0){
                                int x=Marshal.ReadInt32(ptr,offset+12);
                                int y=Marshal.ReadInt32(ptr,offset+16);
                                lock(movementLock)if(Active){
                                    dxPending=(int)Math.Clamp((long)dxPending+x,-2048L,2048L);
                                    dyPending=(int)Math.Clamp((long)dyPending+y,-2048L,2048L);
                                }
                            }
                        }
                    }
                }finally{Marshal.FreeHGlobal(ptr);}
            }
        }
        return DefWindowProc(hWnd,msg,wp,lp);
    }
    async Task SendLoop()
    {
        try{
            while(!cancellation.IsCancellationRequested){
                await Task.Delay(32,cancellation.Token);
                int x,y,version;
                lock(movementLock){
                    if(!Active)continue;
                    version=epoch;
                    x=Math.Clamp(dxPending,-127,127);
                    y=Math.Clamp(dyPending,-127,127);
                    dxPending-=x;dyPending-=y;
                }
                if(x==0 && y==0)continue;
                if(mouse.SubscribedClients.Count==0){ReturnPc("mouse subscriber lost");continue;}
                if(!await notifyGate.WaitAsync(TimeSpan.FromSeconds(2),cancellation.Token)){
                    ReturnPc("BLE notification queue busy");continue;
                }
                try{
                    if(!Active || version!=epoch)continue;
                    // A complete 4-byte mouse report: zero buttons, relative X/Y, zero wheel.
                    byte[] report={0,unchecked((byte)(sbyte)x),unchecked((byte)(sbyte)y),0};
                    var op=mouse.NotifyValueAsync(report.AsBuffer());
                    bool accepted=false;
                    try{
                        var results=await op.AsTask().WaitAsync(TimeSpan.FromSeconds(3),cancellation.Token);
                        accepted=results.Count>0 && results.All(r=>r.Status==GattCommunicationStatus.Success);
                    }catch(TimeoutException){op.Cancel();}
                    if(!accepted)ReturnPc("BLE mouse movement notify rejected or timed out");
                }catch(OperationCanceledException){break;}
                catch(Exception ex){ReturnPc("BLE mouse movement error: "+ex.Message);}
                finally{notifyGate.Release();}
            }
        }catch(OperationCanceledException){}
        catch(Exception ex){ReturnPc("sender exception: "+ex.Message);}
    }
    public void Dispose(){
        ReturnPc("exit");
        cancellation.Cancel();
        if(hookThreadId!=0)PostThreadMessage(hookThreadId,WM_QUIT,IntPtr.Zero,IntPtr.Zero);
    }
    delegate IntPtr HookProc(int code,IntPtr wp,IntPtr lp);
    delegate IntPtr WndProc(IntPtr hwnd,uint msg,IntPtr wp,IntPtr lp);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    struct WNDCLASS {
        public uint style;public WndProc lpfnWndProc;public int cbClsExtra,cbWndExtra;
        public IntPtr hInstance,hIcon,hCursor,hbrBackground;public string? lpszMenuName,lpszClassName;
    }
    [StructLayout(LayoutKind.Sequential)]struct RAWINPUTDEVICE {public ushort usUsagePage,usUsage;public int dwFlags;public IntPtr hwndTarget;}
    [StructLayout(LayoutKind.Sequential)]struct RAWINPUTHEADER {public int dwType,dwSize;public IntPtr hDevice;public IntPtr wParam;}
    [StructLayout(LayoutKind.Sequential)]struct POINT {public int X,Y;}
    [StructLayout(LayoutKind.Sequential)]struct MSLLHOOKSTRUCT {public POINT pt;public uint mouseData,flags,time;public UIntPtr extraInfo;}
    [StructLayout(LayoutKind.Sequential)]struct KBDLLHOOKSTRUCT {public uint vkCode,scanCode,flags,time;public UIntPtr extraInfo;}
    [StructLayout(LayoutKind.Sequential)]struct MSG {public IntPtr hwnd;public uint message;public UIntPtr wParam;public IntPtr lParam;public uint time;public POINT pt;public uint lPrivate;}
    [DllImport("user32.dll",SetLastError=true,CharSet=CharSet.Unicode)]static extern ushort RegisterClass(ref WNDCLASS cls);
    [DllImport("user32.dll",SetLastError=true,CharSet=CharSet.Unicode)]static extern IntPtr CreateWindowEx(uint ex,string cls,string title,uint style,int x,int y,int w,int h,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr param);
    [DllImport("user32.dll")]static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")]static extern IntPtr DefWindowProc(IntPtr hwnd,uint msg,IntPtr wp,IntPtr lp);
    [DllImport("user32.dll",SetLastError=true)]static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] p,uint count,uint size);
    [DllImport("user32.dll",SetLastError=true)]static extern uint GetRawInputData(IntPtr raw,uint command,IntPtr data,ref uint size,uint headerSize);
    [DllImport("user32.dll",SetLastError=true)]static extern IntPtr SetWindowsHookEx(int id,HookProc callback,IntPtr hmod,uint thread);
    [DllImport("user32.dll")]static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr wp,IntPtr lp);
    [DllImport("user32.dll",SetLastError=true)]static extern int GetMessage(out MSG msg,IntPtr hwnd,uint min,uint max);
    [DllImport("user32.dll")]static extern bool PeekMessage(out MSG msg,IntPtr hwnd,uint min,uint max,uint remove);
    [DllImport("user32.dll")]static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")]static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")]static extern bool PostThreadMessage(uint id,uint msg,IntPtr wp,IntPtr lp);
    [DllImport("kernel32.dll")]static extern uint GetCurrentThreadId();
}