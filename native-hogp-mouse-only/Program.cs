using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

static class Program
{
    // Dedicated comparison build: single HID Mouse application collection.
    // It never advertises a keyboard, gamepad, or a second Report characteristic.
    static readonly byte[] MouseReportMap = {
        0x05,0x01, 0x09,0x02, 0xA1,0x01, 0x85,0x01, // Generic Desktop / Mouse, Report ID 1
        0x09,0x01, 0xA1,0x00,                       // Pointer
        0x05,0x09, 0x19,0x01, 0x29,0x03,           // 3 standard mouse buttons
        0x15,0x00, 0x25,0x01, 0x95,0x03,0x75,0x01,0x81,0x02,
        0x95,0x01,0x75,0x05,0x81,0x03,             // padding
        0x05,0x01,0x09,0x30,0x09,0x31,0x09,0x38, // X/Y/Wheel
        0x15,0x81,0x25,0x7F,0x75,0x08,0x95,0x03,0x81,0x06,
        0xC0,0xC0
    };
    // On the wire only X/Y ever change. Buttons and wheel are ALWAYS 0.
    static readonly string LogFile = Path.Combine(AppContext.BaseDirectory,"DeskBridge-MOUSE-ONLY.log");
    static GattServiceProvider? hid,battery;
    static GattLocalCharacteristic? mouse;
    static readonly SemaphoreSlim advChanged = new(0,100);
    static readonly SemaphoreSlim restartGate = new(1,1);
    static int restartCount;
    static Guid Uuid(ushort n)=>Guid.Parse($"0000{n:x4}-0000-1000-8000-00805f9b34fb");
    static void Log(string text) {
        string line=$"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MOUSE ONLY | {text}";
        Console.WriteLine(line);
        try {File.AppendAllText(LogFile,line+Environment.NewLine);}catch{}
    }
    static async Task<GattServiceProvider> CreateService(ushort uuid) {
        var result=await GattServiceProvider.CreateAsync(Uuid(uuid));
        Log($"SERVICE {uuid:X4}: {result.Error}");
        if(result.Error!=BluetoothError.Success || result.ServiceProvider==null)
            throw new InvalidOperationException($"GATT service {uuid:X4} unavailable: {result.Error}");
        if(uuid==0x1812) result.ServiceProvider.AdvertisementStatusChanged+=(_,args)=>{
            Log($"ADVERTISING {args.Status}, error={args.Error}");
            try {advChanged.Release();}catch(SemaphoreFullException){}
        };
        return result.ServiceProvider;
    }
    static async Task<GattLocalCharacteristic> CreateChar(GattServiceProvider svc,ushort uuid,GattLocalCharacteristicParameters par) {
        var r=await svc.Service.CreateCharacteristicAsync(Uuid(uuid),par);
        Log($"CHARACTERISTIC {uuid:X4}: {r.Error}");
        if(r.Error!=BluetoothError.Success || r.Characteristic==null)
            throw new InvalidOperationException($"GATT characteristic {uuid:X4} unavailable: {r.Error}");
        return r.Characteristic;
    }
    static async Task<bool> AdvertiseOnce() {
        if(hid==null) return false;
        while(advChanged.Wait(0)){}
        try {
            if(hid.AdvertisementStatus==GattServiceProviderAdvertisementStatus.Started) return true;
            hid.StartAdvertising(new GattServiceProviderAdvertisingParameters{
                IsDiscoverable=true,IsConnectable=true
            });
        } catch(Exception ex) {Log("ADVERTISEMENT START ERROR: "+ex.Message);return false;}
        var until=DateTime.UtcNow.AddSeconds(8);
        while(DateTime.UtcNow<until) {
            if(hid.AdvertisementStatus==GattServiceProviderAdvertisementStatus.Started) {
                Log("ADVERTISEMENT READY; only HID Mouse advertised");
                return true;
            }
            await advChanged.WaitAsync(TimeSpan.FromMilliseconds(300));
        }
        Log($"ADVERTISEMENT NOT READY: status={hid.AdvertisementStatus}");
        return false;
    }
    static async Task RestartAdvertising(MouseOnlyCapture capture,bool force) {
        if(mouse==null || hid==null)return;
        if(mouse.SubscribedClients.Count>0 && !force) {
            Log("RESTART SKIPPED: active mouse subscriber. Use restart! only when disconnected.");
            return;
        }
        if(!await restartGate.WaitAsync(0)) {Log("RESTART ALREADY RUNNING");return;}
        try {
            capture.ReturnPc("advertising restart");
            Log("RESTART: keep original GATT profile and Windows Bluetooth pairing");
            try{hid.StopAdvertising();}catch(Exception ex){Log("ADVERTISEMENT STOP: "+ex.Message);}
            await Task.Delay(1500);
            bool ok=await AdvertiseOnce();
            if(ok)Interlocked.Increment(ref restartCount);
            Log($"RESTART RESULT: advertising={(ok?"Started":"NotReady")}, count={restartCount}");
        }finally{restartGate.Release();}
    }
    static async Task<int> Main(string[] args) {
        FileStream? guard=null;
        try {
            try {
                guard=new FileStream(Path.Combine(Path.GetTempPath(),"DeskBridge.NativeHOGP.single-instance.lock"),
                    FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            }catch(IOException){Log("ANOTHER DESKBRIDGE HID IS RUNNING. Close it before this test.");Console.ReadLine();return 4;}
            Console.Title="DeskBridge - MOUSE ONLY TEST (movement only)";
            Log("DeskBridge MOUSE ONLY TEST v1.0 - mouse movement ONLY; NO keyboard, NO gamepad");
            Log("Profile is DIFFERENT from composite DeskBridge. Never run both simultaneously.");
            var adapter=await BluetoothAdapter.GetDefaultAsync();
            if(adapter==null || !adapter.IsLowEnergySupported || !adapter.IsPeripheralRoleSupported)
                throw new InvalidOperationException("Windows adapter does not support BLE peripheral mode");
            // Keep same encrypted access as the comparison build.
            var sec=args.Contains("--plain")?GattProtectionLevel.Plain:GattProtectionLevel.EncryptionRequired;
            battery=await CreateService(0x180F);
            await CreateChar(battery,0x2A19,new GattLocalCharacteristicParameters {
                CharacteristicProperties=GattCharacteristicProperties.Read,
                StaticValue=new byte[]{100}.AsBuffer()
            });
            hid=await CreateService(0x1812);
            await CreateChar(hid,0x2A4A,new GattLocalCharacteristicParameters {
                CharacteristicProperties=GattCharacteristicProperties.Read,
                ReadProtectionLevel=sec,
                StaticValue=new byte[]{0x11,0x01,0x00,0x02}.AsBuffer()
            });
            await CreateChar(hid,0x2A4B,new GattLocalCharacteristicParameters {
                CharacteristicProperties=GattCharacteristicProperties.Read,
                ReadProtectionLevel=sec,StaticValue=MouseReportMap.AsBuffer()
            });
            mouse=await CreateChar(hid,0x2A4D,new GattLocalCharacteristicParameters {
                CharacteristicProperties=GattCharacteristicProperties.Read|GattCharacteristicProperties.Notify,
                ReadProtectionLevel=sec,StaticValue=new byte[4].AsBuffer()
            });
            var reportRef=await mouse.CreateDescriptorAsync(Uuid(0x2908),new GattLocalDescriptorParameters {
                ReadProtectionLevel=sec,StaticValue=new byte[]{1,1}.AsBuffer()
            });
            Log("MOUSE REPORT REFERENCE: "+reportRef.Error);
            if(reportRef.Error!=BluetoothError.Success)throw new InvalidOperationException("Mouse Report Reference failed");
            mouse.SubscribedClientsChanged+=(_,_)=>Log("MOUSE SUBSCRIBERS="+mouse.SubscribedClients.Count);
            var cp=await CreateChar(hid,0x2A4C,new GattLocalCharacteristicParameters {
                CharacteristicProperties=GattCharacteristicProperties.WriteWithoutResponse,
                WriteProtectionLevel=sec
            });
            cp.WriteRequested+=async(_,e)=>{
                var def=e.GetDeferral();
                try {
                    var req=await e.GetRequestAsync();
                    if(req!=null && req.Value.Length>0)Log("HID CONTROL POINT: "+req.Value.GetByte(0));
                }catch(Exception ex){Log("CONTROL POINT ERROR: "+ex.Message);}
                finally{def.Complete();}
            };
            Log("PROFILE FROZEN: 1x HID service; 1x Mouse Report ID=1; no Keyboard or Boot characteristics");
            using var capture=new MouseOnlyCapture(mouse,Log);
            if(!await AdvertiseOnce())Log("ADVERTISING FAILED. Console remains usable: type restart");
            Log("F8 PHONE/PC mouse movement; F9 PC emergency. Commands: status, restart, restart!, quit");
            while(true){
                Console.Write("mouse-only> ");
                var cmd=Console.ReadLine()?.Trim().ToLowerInvariant();
                if(cmd==null || cmd=="quit")break;
                if(cmd=="status"){
                    Log($"advertising={hid.AdvertisementStatus}, mouseSubscribers={mouse.SubscribedClients.Count}, capture={(capture.Active?"PHONE":"PC")}, restarts={restartCount}");
                }else if(cmd=="restart" || cmd=="restart!"){
                    await RestartAdvertising(capture,cmd=="restart!");
                }else if(cmd=="help"){
                    Log("Commands: status, restart (safe), restart! (forced advertising), quit; F8 / F9");
                }else{Log("Unknown command; type help");}
            }
            capture.ReturnPc("quit");
            try{hid.StopAdvertising();}catch{}
            return 0;
        }catch(Exception ex){
            Log("FATAL ERROR: "+ex);
            Console.WriteLine("Press Enter to close. Log: "+LogFile);
            Console.ReadLine();
            return 1;
        }finally{guard?.Dispose();}
    }
}