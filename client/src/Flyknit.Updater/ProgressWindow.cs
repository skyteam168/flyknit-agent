using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Flyknit.Updater;

/// <summary>升级时屏幕中间的小窗口：一句话加一根滚动的进度条，换完自动关掉。关不掉、也不用点。</summary>
internal sealed class ProgressWindow : Form
{
    private ProgressWindow(string text)
    {
        Text = "FlyknitBuddy";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(380, 104);
        Font = new Font("Microsoft YaHei UI", 9f);
        Controls.Add(new Label
        {
            Text = text,
            Location = new Point(20, 18),
            Size = new Size(340, 40),
        });
        Controls.Add(new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
            Location = new Point(20, 64),
            Size = new Size(340, 16),
        });
    }

    public static int Run(string text, Func<int> work)
    {
        var code = 0;
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var form = new ProgressWindow(text);
            form.Shown += (_, _) => Task.Run(() =>
            {
                try { code = work(); }
                finally { form.BeginInvoke(form.Close); }
            });
            Application.Run(form);
            return code;
        }
        catch (Exception)
        {
            // 窗口出不来（极少见）也照样升级
            return work();
        }
    }
}
