using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace Estudio.Installer;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new SetupForm());
    }
}

internal sealed class SetupForm : Form
{
    private const string AppName = "Estúdio";
    private const string ExeName = "StudyDesk.Desktop.exe";

    private readonly Panel _termsPage = new() { Dock = DockStyle.Fill };
    private readonly Panel _folderPage = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Panel _progressPage = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Panel _donePage = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly CheckBox _accept = new() { Text = "Li e aceito os termos de uso", AutoSize = true, Left = 20, Top = 335 };
    private readonly TextBox _folder = new() { Left = 20, Top = 70, Width = 440 };
    private readonly CheckBox _desktopShortcut = new() { Text = "Criar atalho na área de trabalho", Checked = true, AutoSize = true, Left = 20, Top = 130 };
    private readonly CheckBox _menuShortcut = new() { Text = "Criar atalho no menu Iniciar", Checked = true, AutoSize = true, Left = 20, Top = 160 };
    private readonly ProgressBar _bar = new() { Left = 20, Top = 80, Width = 540, Height = 22 };
    private readonly Label _status = new() { Left = 20, Top = 115, Width = 540, Height = 40 };
    private readonly CheckBox _launch = new() { Text = "Abrir o Estúdio agora", Checked = true, AutoSize = true, Left = 20, Top = 140 };
    private readonly Label _doneText = new() { Left = 20, Top = 35, Width = 540, Height = 90 };
    private readonly Button _back = new() { Text = "Voltar", Width = 90, Height = 30, Enabled = false };
    private readonly Button _next = new() { Text = "Avançar", Width = 110, Height = 30, Enabled = false };
    private readonly Button _cancel = new() { Text = "Cancelar", Width = 90, Height = 30 };
    private int _step;
    private string? _installedExe;

    public SetupForm()
    {
        Text = $"Instalar {AppName}";
        ClientSize = new Size(600, 440);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        using (var icon = Resource("estudio.ico")) if (icon is not null) Icon = new Icon(icon);

        var buttons = new Panel { Dock = DockStyle.Bottom, Height = 52 };
        _cancel.Left = 490; _cancel.Top = 10;
        _next.Left = 370; _next.Top = 10;
        _back.Left = 270; _back.Top = 10;
        buttons.Controls.AddRange([_back, _next, _cancel]);

        BuildTerms();
        BuildFolder();
        BuildProgress();
        BuildDone();
        Controls.AddRange([_termsPage, _folderPage, _progressPage, _donePage, buttons]);
        buttons.BringToFront();

        _accept.CheckedChanged += (_, _) => _next.Enabled = _accept.Checked;
        _back.Click += (_, _) => Go(_step - 1);
        _next.Click += async (_, _) => await NextAsync();
        _cancel.Click += (_, _) => Close();
        _folder.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Estudio");
    }

    private void BuildTerms()
    {
        _termsPage.Controls.Add(new Label { Text = "Termos de uso", Font = new Font(Font, FontStyle.Bold), Left = 20, Top = 15, AutoSize = true });
        _termsPage.Controls.Add(new Label { Text = "Leia e aceite para continuar.", Left = 20, Top = 38, AutoSize = true });
        var box = new TextBox
        {
            Left = 20, Top = 65, Width = 560, Height = 260, Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical, BackColor = Color.White, Text = ReadText("TERMOS.md")
        };
        box.Select(0, 0);
        _termsPage.Controls.Add(box);
        _termsPage.Controls.Add(_accept);
    }

    private void BuildFolder()
    {
        _folderPage.Controls.Add(new Label { Text = "Pasta de instalação", Font = new Font(Font, FontStyle.Bold), Left = 20, Top = 15, AutoSize = true });
        _folderPage.Controls.Add(new Label { Text = $"O {AppName} será instalado na pasta abaixo:", Left = 20, Top = 42, AutoSize = true });
        var browse = new Button { Text = "Procurar…", Left = 470, Top = 68, Width = 100, Height = 27 };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Escolha a pasta de instalação", UseDescriptionForTitle = true, SelectedPath = _folder.Text };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            var chosen = dialog.SelectedPath.TrimEnd('\\');
            _folder.Text = chosen.EndsWith("Estudio", StringComparison.OrdinalIgnoreCase) ? chosen : Path.Combine(chosen, "Estudio");
        };
        _folderPage.Controls.AddRange([_folder, browse, _desktopShortcut, _menuShortcut]);
        _folderPage.Controls.Add(new Label
        {
            Text = "Seus projetos e progresso ficam em %LOCALAPPDATA%\\Estudio e não são afetados pela pasta escolhida.",
            Left = 20, Top = 205, Width = 560, Height = 40
        });
    }

    private void BuildProgress()
    {
        _progressPage.Controls.Add(new Label { Text = "Instalando…", Font = new Font(Font, FontStyle.Bold), Left = 20, Top = 15, AutoSize = true });
        _progressPage.Controls.AddRange([_bar, _status]);
    }

    private void BuildDone()
    {
        _donePage.Controls.Add(new Label { Text = "Instalação concluída", Font = new Font(Font, FontStyle.Bold), Left = 20, Top = 5, AutoSize = true });
        _donePage.Controls.AddRange([_doneText, _launch]);
    }

    private void Go(int step)
    {
        _step = step;
        _termsPage.Visible = step == 0;
        _folderPage.Visible = step == 1;
        _progressPage.Visible = step == 2;
        _donePage.Visible = step == 3;
        _back.Enabled = step == 1;
        _back.Visible = step < 2;
        _cancel.Enabled = step != 2;
        _next.Text = step switch { 1 => "Instalar", 3 => "Concluir", _ => "Avançar" };
        _next.Enabled = step switch { 0 => _accept.Checked, 2 => false, _ => true };
    }

    private async Task NextAsync()
    {
        switch (_step)
        {
            case 0: Go(1); break;
            case 1: await InstallAsync(); break;
            case 3:
                if (_launch.Checked && _installedExe is not null)
                    Process.Start(new ProcessStartInfo(_installedExe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(_installedExe) });
                Close();
                break;
        }
    }

    private async Task InstallAsync()
    {
        var target = _folder.Text.Trim();
        if (string.IsNullOrWhiteSpace(target) || !Path.IsPathRooted(target))
        {
            MessageBox.Show(this, "Informe um caminho completo, por exemplo C:\\Estudio.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Go(2);
        try
        {
            var desktop = _desktopShortcut.Checked;
            var menu = _menuShortcut.Checked;
            _installedExe = await Task.Run(() => Install(Path.GetFullPath(target), desktop, menu));
            _doneText.Text = $"O {AppName} foi instalado em:\n{Path.GetDirectoryName(_installedExe)}\n\nNa primeira abertura, vá em Conexões de IA para configurar a Claude Code CLI ou uma API.";
            Go(3);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, "Não foi possível instalar:\n\n" + exception.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Go(1);
        }
    }

    private string Install(string target, bool desktop, bool menu)
    {
        using var payload = Resource("app.zip") ?? throw new InvalidOperationException("Pacote do aplicativo não encontrado neste instalador.");
        Directory.CreateDirectory(target);
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        var root = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var total = archive.Entries.Count;
        var done = 0;
        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(target, entry.FullName));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Pacote inválido.");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) Directory.CreateDirectory(destination);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, true);
            }
            done++;
            if (done % 8 == 0 || done == total)
                BeginInvoke(() => { _bar.Value = Math.Min(100, done * 100 / total); _status.Text = $"Copiando arquivos… {done}/{total}"; });
        }

        var exe = Path.Combine(target, ExeName);
        if (!File.Exists(exe)) throw new FileNotFoundException("O pacote não contém " + ExeName);
        File.WriteAllText(Path.Combine(target, "Desinstalar.cmd"), UninstallScript(target), new UTF8Encoding(false));
        BeginInvoke(() => _status.Text = "Criando atalhos…");
        if (desktop) CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"), exe, target);
        if (menu) CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"), exe, target);
        return exe;
    }

    private static string UninstallScript(string target) =>
        "@echo off\r\nchcp 65001 >nul\r\n" +
        "echo Isto remove o Estudio e seus atalhos. Seus projetos em %LOCALAPPDATA%\\Estudio sao mantidos.\r\n" +
        "choice /m \"Continuar\"\r\nif errorlevel 2 exit /b\r\n" +
        "del \"%USERPROFILE%\\Desktop\\Estúdio.lnk\" 2>nul\r\n" +
        "del \"%APPDATA%\\Microsoft\\Windows\\Start Menu\\Programs\\Estúdio.lnk\" 2>nul\r\n" +
        "cd /d \"%TEMP%\"\r\n" +
        $"start \"\" cmd /c \"timeout /t 2 >nul & rmdir /s /q \"{target}\"\"\r\n";

    private static void CreateShortcut(string path, string exe, string workingDirectory)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Atalhos indisponíveis neste Windows.");
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = exe;
        link.WorkingDirectory = workingDirectory;
        link.IconLocation = exe + ",0";
        link.Description = "Estúdio — aplicativo de estudo com IA";
        link.Save();
    }

    private static Stream? Resource(string name) => Assembly.GetExecutingAssembly().GetManifestResourceStream(name);

    private static string ReadText(string name)
    {
        using var stream = Resource(name);
        if (stream is null) return "";
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n").Replace("\n", "\r\n");
    }
}
