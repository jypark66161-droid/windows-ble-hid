using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

static class App
{
    static Guid Id(ushort n) => Guid.Parse($"0000{n:x4}-0000-1000-8000-00805f9b34fb");
    static byte[] ReportMap = {
        0x05,0x01,0x09,0x02,0xA1,0x01,0x09,0x01,0xA1,0x00,
        0x05,0x09,0x19,0x01,0x29,0x03,0x15,0x00,0x25,0x01,
        0x95,0x03,0x75,0x01,0x81,0x02,0x95,0x01,0x75,0x05,0x81,0x03,
        0x05,0x01,0x09,0x30,0x09,0x31,0x09,0x38,0x15,0x81,0x25,0x7F,
        0x75,0x08,0x95,0x03,0x81,0x06,0xC0,0xC0
    };
    static GattServiceProvider? hid,bat,dis;
    static GattLocalCharacteristic? mouse;
    static byte mode=1;
    static void L(string text) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {text}");
    static async Task<GattServiceProvider> Service(ushort id) {
        var r=await GattServiceProvider.CreateAsync(Id(id));
        L($"SERVICE {id:X4}: {r.Error}");
        if(r.ServiceProvider==null || r.Error!=BluetoothError.Success) throw new Exception($"Service {id:X4}: {r.Error}");
        r.ServiceProvider.AdvertisementStatusChanged += (_,e)=>L($"ADV {id:X4}: {e.Status}");
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
    static async Task<int> Main(string[] args) {
        try {
            L("DeskBridge Native HOGP v0.1 - independent Windows BLE mouse proof of concept");
            var adapter=await BluetoothAdapter.GetDefaultAsync();
            if(adapter==null || !adapter.IsLowEnergySupported || !adapter.IsPeripheralRoleSupported) {
                L("ERROR: BLE peripheral mode unavailable"); return 2;
            }
            var sec=args.Contains("--plain") ? GattProtectionLevel.Plain : GattProtectionLevel.EncryptionRequired;
            bat=await Service(0x180F);
            await Readonly(bat,0x2A19,new byte[]{100});
            dis=await Service(0x180A);
            await Readonly(dis,0x2A29,Encoding.UTF8.GetBytes("DeskBridge Experimental"));
            await Readonly(dis,0x2A24,Encoding.UTF8.GetBytes("Native Mouse v0.1"));
            await Readonly(dis,0x2A50,new byte[]{2,0xFF,0xFF,1,0,1,0});
            hid=await Service(0x1812);
            await Readonly(hid,0x2A4A,new byte[]{0x11,0x01,0,0x02},sec);
            await Readonly(hid,0x2A4B,ReportMap,sec);
            mouse=await Characteristic(hid,0x2A4D,new GattLocalCharacteristicParameters {
                CharacteristicProperties=GattCharacteristicProperties.Read|GattCharacteristicProperties.Notify,
                ReadProtectionLevel=sec, StaticValue=new byte[4].AsBuffer()
            });
            mouse.SubscribedClientsChanged += (_,_)=>L($"SUBSCRIBERS={mouse.SubscribedClients.Count}");
            var d=await mouse.CreateDescriptorAsync(Id(0x2908),new GattLocalDescriptorParameters{
                ReadProtectionLevel=sec, StaticValue=new byte[]{0,1}.AsBuffer()
            });
            L($"DESCRIPTOR 2908: {d.Error}");
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
            bat.StartAdvertising(new GattServiceProviderAdvertisingParameters{IsDiscoverable=false,IsConnectable=true});
            dis.StartAdvertising(new GattServiceProviderAdvertisingParameters{IsDiscoverable=false,IsConnectable=true});
            hid.StartAdvertising(new GattServiceProviderAdvertisingParameters{IsDiscoverable=true,IsConnectable=true});
            L($"ADVERTISING HID={hid.AdvertisementStatus}");
            L("Pair phone, then commands: status, right, left, up, down, click, quit");
            while(true) {
                Console.Write("hogp> ");
                var cmd=Console.ReadLine()?.Trim().ToLowerInvariant();
                if(cmd==null || cmd=="quit")break;
                if(cmd=="status"){L($"HID={hid.AdvertisementStatus}, subscribed={mouse.SubscribedClients.Count}, protocol={mode}");continue;}
                var (buttons,dx,dy,wheel) = cmd switch {
                    "right"=>(0,12,0,0),"left"=>(0,-12,0,0),"up"=>(0,0,-12,0),
                    "down"=>(0,0,12,0),"click"=>(1,0,0,0),_=>(0,0,0,0)
                };
                if(mouse.SubscribedClients.Count==0){L("NO INPUT SUBSCRIBER. Device may not be recognized as HID.");continue;}
                var result=await mouse.NotifyValueAsync(new byte[]{(byte)buttons,unchecked((byte)dx),unchecked((byte)dy),unchecked((byte)wheel)}.AsBuffer());
                L($"NOTIFY recipients={result.Count}, status={string.Join(",",result.Select(x=>x.Status))}");
                if(cmd=="click") {await Task.Delay(35);await mouse.NotifyValueAsync(new byte[4].AsBuffer());}
            }
            hid.StopAdvertising();bat.StopAdvertising();dis.StopAdvertising();return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
