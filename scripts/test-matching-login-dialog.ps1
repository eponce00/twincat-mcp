param([Parameter(Mandatory=$true)][string]$Automation)
$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Automation).Path)
Add-Type -ReferencedAssemblies System.Windows.Forms,System.Drawing -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;
public static class LoginDialogTest {
    public static void Run(Assembly assembly, string message, bool expectedCancel, int pidOffset) {
        bool cancelled = false, approved = false;
        using (var form = new Form())
        using (var timer = new Timer()) {
            form.Opacity = 0; form.ShowInTaskbar = false;
            form.Controls.Add(new Label { Text = message, Width = 300 });
            var cancel = new Button { Text = "&Cancel", Top = 40 };
            var yes = new Button { Text = "&Yes", Top = 80 };
            cancel.Click += (s, e) => { cancelled = true; form.Close(); };
            yes.Click += (s, e) => { approved = true; form.Close(); };
            form.Controls.Add(cancel); form.Controls.Add(yes);
            timer.Interval = 3000; timer.Tick += (s, e) => form.Close(); timer.Start();
            var type = assembly.GetType("TcAutomation.Commands.RejectDownloadDialogs", true);
            using ((IDisposable)Activator.CreateInstance(type, new object[] { Process.GetCurrentProcess().Id + pidOffset }))
                Application.Run(form);
        }
        if (cancelled != expectedCancel || approved) throw new Exception("Unexpected dialog action: " + message);
    }
}
'@
foreach ($message in @('Download the application?', 'Login with online change?', 'Overwrite the existing application?')) {
    [LoginDialogTest]::Run($assembly, $message, $true, 0)
}
[LoginDialogTest]::Run($assembly, 'Download the application?', $false, 1)
[LoginDialogTest]::Run($assembly, 'Unrelated operation', $false, 0)
Write-Output 'PASS: three owned change dialogs cancelled; other PIDs and unrelated dialogs untouched; no approval clicks.'
