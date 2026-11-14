using MGRemote.Networking;

void Check(bool valid) { if (!valid) throw new Exception("LAN scope/OS hint check failed."); }
LanAdapter Adapter(string ip,int prefix) => new("fixture",ip,prefix,"");
var normal=LanDiscovery.Scope(Adapter("192.168.10.5",24));
Check(normal.Length==254&&normal[0].ToString()=="192.168.10.1"&&normal[^1].ToString()=="192.168.10.254");
var broad=LanDiscovery.Scope(Adapter("10.2.3.0",16));
Check(broad.Length==256&&broad[0].ToString()=="10.2.3.0"&&broad[^1].ToString()=="10.2.3.255");
Check(LanDiscovery.Scope(Adapter("172.16.2.3",31)).Length==2&&LanDiscovery.Scope(Adapter("172.16.2.3",32)).Length==1);
foreach(var item in new[]{Adapter("8.8.8.8",24),Adapter("2001:db8::1",64),Adapter("192.168.1.1",33)}) {
 bool rejected=false;try{LanDiscovery.Scope(item);}catch(ArgumentException){rejected=true;}Check(rejected);
}
Check(LanDiscovery.Guess("macbook.local",0).Contains("probable")&&LanDiscovery.Guess("",64).Contains("autre")&&LanDiscovery.Guess("",0)=="Indéterminé");
using var cancel=new CancellationTokenSource();cancel.Cancel();bool stopped=false;
try{await LanDiscovery.DiscoverAsync(Adapter("192.168.10.5",24),false,null,cancel.Token);}catch(OperationCanceledException){stopped=true;}Check(stopped);
Console.WriteLine("PASS: private IPv4 scope, /24 cap, subnet edges, /31-/32, public address rejection, uncertain OS hints and cancellation; no network scan performed.");
if(args.Contains("--known-neighbors")) {
 var adapters=LanDiscovery.Adapters();
 if(adapters.Length>0) { var devices=await LanDiscovery.DiscoverAsync(adapters[0],false,null,CancellationToken.None);Check(devices.Any(d=>d.Address==adapters[0].Address));Console.WriteLine("PASS: live passive neighbor discovery completed; "+devices.Length+" device(s), addresses omitted."); }
 else Console.WriteLine("SKIP: no private IPv4 Ethernet/Wi-Fi interface.");
}
