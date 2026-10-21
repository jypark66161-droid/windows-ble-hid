using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

static class App
{
    static Guid Id(ushort n) => Guid.Parse($"0000{n:x4}-0000-1000-8000-00805f9b34fb");
    static byte[] ReportMap = {
        // Mouse: Report ID 1, buttons / X / Y / wheel
        0x05,0x01,0x09,0x02,0xA1,0x01,0x85,0x01,0x09,0x01,0xA1,0x00,
        0x05,0x09,0x19,0x01,0x29,0x03,0x15,0x00,0x25,0x01,
        0x95,0x03,0x75,0x01,0x81,0x02,0x95,0x01,0x75,0x05,0x81,0x03,
        0x05,0x01,0x09,0x30,0x09,0x31,0x09,0x38,0x15,0x81,0x25,0x7F,
        0x75,0x08,0x95,0x03,0x81,0x06,0xC0,0xC0,
        // Keyboard: Report ID 2, 8 modifiers, reserved byte, 6 key usages
        0x05,0x01,0x09,0x06,0xA1,0x01,0x85,0x02,
        0x05,0x07,0x19,0xE0,0x29,0xE7,0x15,0x00,0x25,0x01,
        0x75,0x01,0x95,0x08,0x81,0x02,
        0x95,0x01,0x75,0x08,0x81,0x01,
        0x95,0x06,0x75,0x08,0x15,0x00,0x25,0x65,
        0x05,0x07,0x19,0x00,0x29,0x65,0x81,0x00,0xC0
    };
    static GattServiceProvider? hid,bat;
    static GattLocalCharacteristic? mouse, keyboard, bootMouse, bootKeyboard;
    static byte mode=1;
    static readonly SemaphoreSlim advSignal = new(0, 100);
    static int advErrors;
    static int recoveryInProgress;
    static int recoveryCount;
    static long lastSubscriberChange=Environment.TickCount64;
    static bool HasSubscribers() => (mouse?.SubscribedClients.Count??0)>0 || (keyboard?.SubscribedClients.Count??0)>0 || (bootMouse?.SubscribedClients.Count??0)>0 || (bootKeyboard?.SubscribedClients.Count??0)>0;
    static void SubscriberChanged(string kind,int count) {
        Interlocked.Exchange(ref lastSubscriberChange,Environment.TickCount64);
        L("HID SUBSCRIPTION "+kind+"="+count+", any="+HasSubscribers());
    }
    static async Task RecoverAdvertising() {
        if(hid==null || HasSubscribers() || hid.AdvertisementStatus!=GattServiceProviderAdvertisementStatus.Aborted ||
           Environment.TickCount64-Interlocked.Read(ref lastSubscriberChange)<10000 ||
           Interlocked.CompareExchange(ref recoveryInProgress,1,0)!=0) return;
        try {
            L("ADV RECOVERY: Aborted and no HID subscribers; restart advertising");
            hid.StopAdvertising();
            await Task.Delay(1200);
            if(HasSubscribers()) return;
            hid.StartAdvertising(new GattServiceProviderAdvertisingParameters{IsDiscoverable=true,IsConnectable=true});
            await Task.Delay(1000);
            L("ADV RECOVERY RESULT="+hid.AdvertisementStatus);
            if(hid.AdvertisementStatus==GattServiceProviderAdvertisementStatus.Started) Interlocked.Increment(ref recoveryCount);
        }
        catch(Exception ex) {L("ADV RECOVERY ERROR: "+ex.Message);}
        finally {Volatile.Write(ref recoveryInProgress,0);}
    }

    static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "DeskBridge-HOGP-startup.log");
    static void L(string text) {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {text}";
        Console.WriteLine(line);
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
    }
    static void PauseOnError() {
        Console.WriteLine();
        Console.WriteLine("STARTUP FAILED. This window will remain open until you press Enter.");
        Console.WriteLine("Log: " + LogPath);
        try { Console.ReadLine(); } catch { }
    }
    static async Task<GattServiceProvider> Service(ushort id) {
        var r=await GattServiceProvider.CreateAsync(Id(id));
        L($"SERVICE {id:X4}: {r.Error}");
        if(r.ServiceProvider==null || r.Error!=BluetoothError.Success) throw new Exception($"Service {id:X4}: {r.Error}");
        r.ServiceProvider.AdvertisementStatusChanged += (_,e)=> {
            L($"ADV {id:X4}: {e.Status}, error={e.Error}");
            if(id==0x1812) { if(e.Status==GattServiceProviderAdvertisementStatus.Aborted) Interlocked.Increment(ref advErrors); try { advSignal.Release(); } catch(SemaphoreFullException) {} }
        };
        return r.ServiceProvider;
    }
    static async Task<GattLocalCharacteristic> Characteristic(GattServiceProvider svc,ushort id,GattLocalCharacteristicParameters par) {
        var r=await svc.Service.CreateCharacteristicAsync(Id(id),par);
        L($"CHAR {id:X4}: {r.Error}");
        if(r.Characteristic==null || r.Error!=BluetoothError.Success) throw new Exception($"Char {id:X4}: {r.Error}");
        return r.Characteristic;
    }
    static async Task Readonly(GattServiceProvider s,ushort id,byte[] data,GattProtectionLevel sec=GattProtectionLevel.Plain) =>
       await Characteristic(s,id,new GattLocalCharacteristicParameters{
           CharacteristicProperties=GattCharacteristicProperties.Read,ReadProtectionLevel=sec,StaticValue=data.AsBuffer()
       });
    static async Task<bool> EnsureHidAdvertising() {
        for(int attempt=1;attempt<=3;attempt++) {
            L($"HID ADVERTISING START attempt={attempt}/3");
            while(advSignal.Wait(0)) {}
            try {
                if(hid!.AdvertisementStatus==GattServiceProviderAdvertisementStatus.Started) return true;
                hid.StartAdvertising(new GattServiceProviderAdvertisingParameters{IsDiscoverable=true,IsConnectable=true});
            } catch(Exception ex) { L($"HID START EXCEPTION: {ex}"); }
            var deadline=DateTime.UtcNow.AddSeconds(10);
            while(DateTime.UtcNow<deadline) {
                var current=hid!.AdvertisementStatus;
                if(current==GattServiceProviderAdvertisementStatus.Started) {
                    L($"HID ADVERTISING READY attempt={attempt}, status={current}");
                    return true;
                }
                var left=deadline-DateTime.UtcNow;
                if(left<=TimeSpan.Zero) break;
                await advSignal.WaitAsync(left<TimeSpan.FromMilliseconds(500)?left:TimeSpan.FromMilliseconds(500));
            }
            L($"HID ADVERTISING NOT READY attempt={attempt}, status={hid!.AdvertisementStatus}");
            if(attempt<3) {
                try { hid.StopAdvertising(); } catch(Exception ex) { L($"HID STOP EXCEPTION: {ex.Message}"); }
                await Task.Delay(1200);
            }
        }
        return false;
    }
    static async Task<int> Main(string[] args) {
        try {
            // Single-instance ownership must be established before creating GATT services.
            FileStream? instanceGuard=null;
            try {
                instanceGuard = new FileStream(Path.Combine(Path.GetTempPath(),"DeskBridge.NativeHOGP.single-instance.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            } catch (IOException) {
                L("ALREADY RUNNING: another DeskBridge Native HOGP process owns BLE HID. Close the older program first.");
                PauseOnError(); return 4;
            }
            try {
            L("DeskBridge Native HOGP v0.11 - connection lifecycle recovery");
            var adapter=await BluetoothAdapter.GetDefaultAsync();
            if(adapter==null || !adapter.IsLowEnergySupported || !adapter.IsPeripheralRoleSupported) {
                L("ERROR: BLE peripheral mode unavailable"); PauseOnError(); return 2;
            }
            L($"PROCESS ID={Environment.ProcessId}, instance=exclusive");
            var sec=args.Contains("--plain") ? GattProtectionLevel.Plain : GattProtectionLevel.EncryptionRequired;
            bat=await Service(0x180F);
            await Readonly(bat,0x2A19,new byte[]{100});
            L("INFO: 180A Device Information is Windows-reserved; skipping app-created 180A.");
            hid=await Service(0x1812);
            await Readonly(hid,0x2A4A,new byte[]{0x11,0x01,0,0x02},sec);
            await Readonly(hid,0x2A4B,ReportMap,sec);
            mouse=await Characteristic(hid,0x2A4D,new GattLocalCharacteristicParameters {
                CharacteristicProperties=GattCharacteristicProperties.Read|GattCharacteristicProperties.Notify,
                ReadProtectionLevel=sec, StaticValue=new byte[4].AsBuffer()
            });
            mouse.SubscribedClientsChanged += (_,_)=>SubscriberChanged("mouse",mouse.SubscribedClients.Count);
            var d=await mouse.CreateDescriptorAsync(Id(0x2908),new GattLocalDescriptorParameters{
                ReadProtectionLevel=sec, StaticValue=new byte[]{1,1}.AsBuffer()
            });
            L($"MOUSE REPORT REF 2908: {d.Error}");
            keyboard=await Characteristic(hid,0x2A4D,new GattLocalCharacteristicParameters {
                CharacteristicProperties=GattCharacteristicProperties.Read|GattCharacteristicProperties.Notify,
                ReadProtectionLevel=sec, StaticValue=new byte[8].AsBuffer()
            });
            keyboard.SubscribedClientsChanged += (_,_)=>SubscriberChanged("keyboard",keyboard.SubscribedClients.Count);
            var kd=await keyboard.CreateDescriptorAsync(Id(0x2908),new GattLocalDescriptorParameters{
                ReadProtectionLevel=sec,StaticValue=new byte[]{2,1}.AsBuffer()
            });
            L($"KEYBOARD REPORT REF 2908: {kd.Error}");
            // Boot Protocol input endpoints: additional standard HOGP entry points for hosts
            // that prefer boot-compatible mouse/keyboard at first pairing.
            bootMouse=await Characteristic(hid,0x2A33,new GattLocalCharacteristicParameters {
                CharacteristicProperties=GattCharacteristicProperties.Read|GattCharacteristicProperties.Notify,
                ReadProtectionLevel=sec, StaticValue=new byte[3].AsBuffer()
            });
            bootKeyboard=await Characteristic(hid,0x2A22,new GattLocalCharacteristicParameters {
                CharacteristicProperties=GattCharacteristicProperties.Read|GattCharacteristicProperties.Notify,
                ReadProtectionLevel=sec, StaticValue=new byte[8].AsBuffer()
            });
            bootMouse.SubscribedClientsChanged+=(_,_)=>SubscriberChanged("bootMouse",bootMouse.SubscribedClients.Count);
            bootKeyboard.SubscribedClientsChanged+=(_,_)=>SubscriberChanged("bootKeyboard",bootKeyboard.SubscribedClients.Count);
            var pm=await Characteristic(hid,0x2A4E,new GattLocalCharacteristicParameters{
                CharacteristicProperties=GattCharacteristicProperties.Read|GattCharacteristicProperties.WriteWithoutResponse,
                ReadProtectionLevel=sec,WriteProtectionLevel=sec
            });
            pm.ReadRequested+=async (_,e)=> {
                var def=e.GetDeferral(); try { var req=await e.GetRequestAsync();req.RespondWithValue(new byte[]{mode}.AsBuffer());} catch(Exception ex){L(ex.Message);} finally{def.Complete();}
            };
            pm.WriteRequested+=async (_,e)=> {
                var def=e.GetDeferral();try {var req=await e.GetRequestAsync(); var rd=DataReader.FromBuffer(req.Value); if(rd.UnconsumedBufferLength>0)mode=rd.ReadByte();L($"MODE={mode}");}catch(Exception ex){L(ex.Message);}finally{def.Complete();}
            };
            var ctrl=await Characteristic(hid,0x2A4C,new GattLocalCharacteristicParameters{
                CharacteristicProperties=GattCharacteristicProperties.WriteWithoutResponse,WriteProtectionLevel=sec
            });
            ctrl.WriteRequested+=async (_,e)=> {
                var def=e.GetDeferral();try {var req=await e.GetRequestAsync(); L("CONTROL POINT WRITE");}catch(Exception ex){L(ex.Message);}finally{def.Complete();}
            };
            L("BATTERY GATT REGISTERED; battery advertising disabled (HID service only).");
            while(!await EnsureHidAdvertising()) {
                L("Advertising unavailable; retrying in 5 seconds without relaunch.");
                await Task.Delay(5000);
            }
            using var capture = new NativeInputCapture(mouse, keyboard, L, bootMouse, bootKeyboard, () => mode);
            L("Pair phone. F8 toggles PHONE/PC input; F9 emergency PC return.");
            L("Commands: status, right, left, up, down, click, a, enter, space, quit");
            using var checkTimer = new System.Threading.Timer(_ => { try { capture.HealthCheck(); _=RecoverAdvertising(); } catch(Exception ex) { L("HEALTH ERROR: " + ex.Message); } }, null, 1000, 700);
            while(true) {
                Console.Write("hogp> ");
                var cmd=Console.ReadLine()?.Trim().ToLowerInvariant();
                if(cmd==null || cmd=="quit")break;
                if(cmd=="status"){L($"HID={hid.AdvertisementStatus}, mouse={mouse.SubscribedClients.Count}, keyboard={keyboard.SubscribedClients.Count}, capture={(capture.Active ? "PHONE" : "PC")}, protocol={mode}, bootMouse={bootMouse!.SubscribedClients.Count}, bootKeyboard={bootKeyboard!.SubscribedClients.Count}, abortedEvents={advErrors}, advRecoveries={recoveryCount}");continue;}
                if(cmd=="a" || cmd=="enter" || cmd=="space"){
                    if(keyboard.SubscribedClients.Count==0){L("NO KEYBOARD SUBSCRIBER");continue;}
                    byte usage=cmd=="a"?(byte)0x04:cmd=="enter"?(byte)0x28:(byte)0x2C;
                    await keyboard.NotifyValueAsync(new byte[]{0,0,usage,0,0,0,0,0}.AsBuffer());
                    await Task.Delay(40);
                    await keyboard.NotifyValueAsync(new byte[]{0,0,0,0,0,0,0,0}.AsBuffer());
                    L($"KEY SENT {cmd}");continue;
                }
                var (buttons,dx,dy,wheel) = cmd switch {
                    "right"=>(0,12,0,0),"left"=>(0,-12,0,0),"up"=>(0,0,-12,0),
                    "down"=>(0,0,12,0),"click"=>(1,0,0,0),_=>(0,0,0,0)
                };
                if(mouse.SubscribedClients.Count==0){L("NO INPUT SUBSCRIBER. Device may not be recognized as HID.");continue;}
                var result=await mouse.NotifyValueAsync(new byte[]{(byte)buttons,unchecked((byte)dx),unchecked((byte)dy),unchecked((byte)wheel)}.AsBuffer());
                L($"NOTIFY recipients={result.Count}, status={string.Join(",",result.Select(x=>x.Status))}");
                if(cmd=="click") {await Task.Delay(35);await mouse.NotifyValueAsync(new byte[]{0,0,0,0}.AsBuffer());}
            }
            capture.ReturnLocal("quit");
            hid.StopAdvertising();return 0;
            } finally { instanceGuard?.Dispose(); }
        }catch(Exception ex){L("FATAL ERROR: " + ex); PauseOnError(); return 1;}
    }
}
