namespace Bf4Gui;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--self-test"])
        {
            ApplicationConfiguration.Initialize();
            using var form = new MainForm();
            form.CreateControl();
            return Bf4Toolkit.Program.Main(["--help"]);
        }
        if (args is ["--workflow-test"])
        {
            ApplicationConfiguration.Initialize();
            var result = 2;
            using var form = new MainForm { ShowInTaskbar = false, WindowState = FormWindowState.Minimized };
            form.Shown += async (_, _) =>
            {
                try { await form.RunDiagnosticAsync(); result = 0; }
                catch { result = 2; }
                finally { form.Close(); }
            };
            Application.Run(form);
            return result;
        }
        if (args is ["--screenshot", var path])
        {
            ApplicationConfiguration.Initialize();
            using var form = new MainForm();
            form.Show();
            Application.DoEvents();
            form.PerformLayout();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(path);
            static IEnumerable<Control> Walk(Control root) =>
                root.Controls.Cast<Control>().SelectMany(child =>
                    new[] { child }.Concat(Walk(child)));
            File.WriteAllText(path + ".txt", string.Join("\n",
                Walk(form).OfType<ComboBox>().Select(combo =>
                    $"{combo.Items.Count} items, selected={combo.SelectedIndex}, text={combo.Text}")));
            return 0;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
