using DeskBuddy.Core;
using System.IO;
using System.Security.Cryptography;

namespace DeskBuddy.App;
public sealed partial class MainWindow
{
    async Task RunInteractionSmoke(string directory, Action<bool,string> check, List<string> images)
    {
        await ConnectDemo(); await ReadDevice(); page=2; BuildShell();
        EditProfile(p=>p with { Mode=HomeMode.DeskPet, Face=Face.Fox, Animation=false });
        await ApplyBankOnly(false,lifetime.Token);
        foreach(bool on in new[]{true,false})
        {
            await Control("OUTPUT "+(on?"1":"0"));
            int commandStart=demoTransport!.Commands.Count;
            foreach(var action in Enum.GetValues<AnimationPreviewAction>())
            {
                await StartAnimation(action);
                check(client!.Status!.Armed==on,$"R3E {action} preserves {(on?"ON":"OFF")} through draft apply and animation");
                await StopAnimation();
                check(client.Status.Armed==on,$"R3E {action} stop preserves prior output");
            }
            await ApplyBank(true); check(client!.Status!.Armed==on,"R3E Save preserves output "+on);
            await Revert(); check(client.Status.Armed==on,"R3E Revert preserves output "+on);
            await Control("ROUTE "+client.Status.Route); check(client.Status.Armed==on,"R3E same route preserves output "+on);
            await Control("PAGE HOME"); check(client.Status.Armed==on,"R3E Home preserves output "+on);
            check(!demoTransport.Commands.Skip(commandStart).Any(s=>s=="CONTROL OUTPUT 1"),"UI never rearms output after edit or animation "+on);
        }
        await Control("OUTPUT 1");
        await StartAnimation(AnimationPreviewAction.Walk);
        demoTransport!.Turn(-1); await Refresh(); TickAnimation();
        check(client!.Status!.Armed && client.Status.DemoAction==null && client.Status.PetDirection==-1 && !animationPlayback.Running,"real-input simulator takes over demo while preserving output");
        int pageCommands = demoTransport.Commands.Count;
        await Control("PAGE DIAG"); check(client.Status.Armed && client.Status.HidSuppressed,"R3F diagnostic preserves switch ON and pauses HID");
        check(outputPill!.Text.Contains(chinese ? "暂停" : "PAUSED"),"diagnostic output pill distinguishes ON from paused sending");
        await Control("PAGE HOME"); check(client.Status.Armed && !client.Status.HidSuppressed,"home clears pause without changing switch ON");
        check(!demoTransport.Commands.Skip(pageCommands).Any(s=>s=="CONTROL OUTPUT 1"),"page return never rearms through a hidden command");
        static string Pixels(DevicePreview v){var b=RenderNativePreview(v);var bytes=new byte[320*240*4];b.CopyPixels(bytes,1280,0);return Convert.ToHexString(SHA256.HashData(bytes));}
        foreach(int mode in new[]{0,1,2})
        {
            var hashes=new HashSet<string>();
            foreach(int direction in new[]{-1,1}) foreach(uint elapsed in new uint[]{0,180,900})
            {
                var v=new DevicePreview{Width=320,Height=240,Mode=mode,Theme=4,Face=8,Animate=false,PhysicalFeedback=true,PetActive=true,PetMotion=2,PetDirection=direction,PetElapsedMilliseconds=elapsed};
                hashes.Add(Pixels(v));
                string name=$"physical-mode{mode}-direction{direction}-{elapsed}ms.png";
                Capture(v,Path.Combine(directory,name));images.Add(name);
            }
            check(hashes.Count>=3,$"mode{mode}: physical response changes rendered pixels with automatic idle OFF");
        }
        var known=new DevicePreview{Width=320,Height=240,Armed=false};var unknown=new DevicePreview{Width=320,Height=240,Armed=false,StatusKnown=false};
        check(Pixels(known)!=Pixels(unknown),"unknown miniature output visibly differs from OFF");
        // Exercise the real DispatcherTimer on Device (not the Appearance page,
        // which already refreshes at 120 ms). Keep transport polling stopped so
        // changing pixels must come from the local clock, not newer snapshots.
        page=0;BuildShell();EditProfile(p=>p with { Mode=HomeMode.DeskPet,Face=Face.Fox,Animation=false });
        await ApplyBankOnly(false,lifetime.Token);
        demoTransport.Turn(1);await Refresh();await Settle();
        var livePreview=previews.Single();
        check(page==0&&!livePreview.Animate&&livePreview.PhysicalFeedback&&livePreview.PetActive==true,"device-page timer fixture has physical feedback with automatic idle OFF");
        check(poll.Interval==TimeSpan.FromSeconds(1)&&blinkTimer.Interval==TimeSpan.FromMilliseconds(120)&&!poll.IsEnabled,"local animation uses 120 ms while transport polling remains one second and stopped for this test");
        var observedStatus=client.Status;int timerCommands=demoTransport.Commands.Count;
        double firstAge=livePreview.PetStatusAgeMilliseconds;
        var timerHashes=new HashSet<string>{Pixels(livePreview)};
        blinkTimer.Start();
        try
        {
            for(int sample=0;sample<6;++sample)
            {
                await Task.Delay(125);await Settle();timerHashes.Add(Pixels(livePreview));
                if(sample is 0 or 2 or 5)
                {
                    string name=$"device-page-physical-idle-off-timer-{sample}.png";
                    Capture(livePreview,Path.Combine(directory,name));images.Add(name);
                }
            }
            check(livePreview.PetStatusAgeMilliseconds>firstAge+240,"real DispatcherTimer advances local feedback age on Device page");
            check(timerHashes.Count>=3,"real Device-page timer changes rendered physical pixels with automatic idle OFF");
            check(ReferenceEquals(client.Status,observedStatus)&&demoTransport.Commands.Count==timerCommands,"local animation neither polls transport nor fabricates a fresh snapshot");
            demoTransport.SimulateIdleFor(TimeSpan.FromSeconds(2));await Refresh();
            string inactive=Pixels(livePreview);await Task.Delay(300);await Settle();
            check(livePreview.PetActive==false&&Pixels(livePreview)==inactive,"inactive device readback stops physical animation; local timer does not extend activity");
            demoTransport.Turn(1);await Refresh();client.StatusStaleAfter=TimeSpan.FromMilliseconds(100);
            timerCommands=demoTransport.Commands.Count;await Task.Delay(300);await Settle();
            check(!Fresh&&livePreview.PetActive==null&&!livePreview.StatusKnown&&demoTransport.Commands.Count==timerCommands,"local timer expires stale active feedback into unknown without new traffic or synthesized input");
        }
        finally {blinkTimer.Stop();client.StatusStaleAfter=TimeSpan.FromSeconds(3);}
        await ConnectDemo();await ReadDevice();
    }
}
