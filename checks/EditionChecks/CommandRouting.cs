using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using SonicRoute;

internal static partial class Program
{
    private static void CheckCommandRouting(string output)
    {
        using var input=typeof(App).Assembly.GetManifestResourceStream("SonicRoute.Resources.RuleCommand.ps1")!;
        string script=new StreamReader(input).ReadToEnd();
        string prefix="EditionFixture"+Guid.NewGuid().ToString("N")+"_";
        foreach(string edition in new[]{"Full","Lite","Legacy","Store"}) script=script.Replace("'"+edition+"'","'"+prefix+edition+"'");
        string path=Path.Combine(output,"sr-fixture.ps1"); File.WriteAllText(path,script,new UTF8Encoding(true));
        foreach(string? option in new string?[]{null,"--lite","--full"})
        {
            string chosen=option=="--lite"?"Lite":"Full";
            string sid=WindowsIdentity.GetCurrent().User!.Value;
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            // Only randomized fixture endpoints exist here. No product pipe or automation rule is contacted.
            using var server=new NamedPipeServerStream($"SonicRoute.RuleCommand.{prefix}{chosen}.{Process.GetCurrentProcess().SessionId}.{sid}",
                PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
            using var alternate=option==null?new NamedPipeServerStream($"SonicRoute.RuleCommand.{prefix}Lite.{Process.GetCurrentProcess().SessionId}.{sid}",
                PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous):null;
            var alternateWait=alternate?.WaitForConnectionAsync(timeout.Token);
            var exchange=Task.Run(async()=>
            {
                await server.WaitForConnectionAsync(timeout.Token);
                byte[] header=new byte[4]; await server.ReadExactlyAsync(header,timeout.Token);
                byte[] body=new byte[BitConverter.ToInt32(header)]; await server.ReadExactlyAsync(body,timeout.Token);
                if(Encoding.UTF8.GetString(body)!="fixture rule") throw new Exception("Wrong command selector");
                byte[] response=JsonSerializer.SerializeToUtf8Bytes(new{ExitCode=0,RuleName=chosen});
                await server.WriteAsync(BitConverter.GetBytes(response.Length),timeout.Token);await server.WriteAsync(response,timeout.Token);
            });
            var info=new ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string arg in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",path})info.ArgumentList.Add(arg);
            if(option!=null)info.ArgumentList.Add(option); info.ArgumentList.Add("fixture rule");
            using var process=Process.Start(info)!;
            var text=process.StandardOutput.ReadToEndAsync(); var error=process.StandardError.ReadToEndAsync();
            try
            {
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult(); exchange.GetAwaiter().GetResult();
                string stdout=text.GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(output,"sr-"+(option??"default")+"-stdout.txt"),stdout);
                using var response=JsonDocument.Parse(stdout);
                Check(process.ExitCode==0 && response.RootElement.GetProperty("RuleName").GetString()==chosen && alternate?.IsConnected!=true,
                    "sr "+(option??"default")+" routes to "+chosen+" using randomized local fixture pipes");
                if(process.ExitCode!=0)throw new Exception(error.GetAwaiter().GetResult());
            }
            finally
            {
                timeout.Cancel();
                if(!process.HasExited)process.Kill(true); // Only this check's own PowerShell child.
                if(alternateWait!=null)try{alternateWait.GetAwaiter().GetResult();}catch(OperationCanceledException){}
            }
        }
    }
}
