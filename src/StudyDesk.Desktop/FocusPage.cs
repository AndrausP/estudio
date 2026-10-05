using Avalonia.Controls;
using Avalonia.Media;
using StudyDesk.Domain;
using System.Text.Json;

namespace StudyDesk.Desktop;

public partial class MainWindow
{
    private TextBlock? _focusClock;
    private TextBlock? _focusDescription;
    private int _cycleBaseSeconds;
    private sealed class FocusPreference
    {
        public int FocusMinutes { get; set; } = 25;
        public int BreakMinutes { get; set; } = 5;
        public int CycleBaseSeconds { get; set; }
    }

    private string FocusKey => $"{_project?.Id:N}:{_module?.Id.ToString("N") ?? "projeto"}";
    private string FocusSettingsPath => Path.Combine(_dataDirectory, "focus-settings.json");

    private void LoadFocusPreferences()
    {
        try
        {
            var all = File.Exists(FocusSettingsPath)
                ? JsonSerializer.Deserialize<Dictionary<string, FocusPreference>>(File.ReadAllText(FocusSettingsPath))
                : null;
            if (all is not null && all.TryGetValue(FocusKey, out var preferences))
            {
                _focusLength = Math.Clamp(preferences.FocusMinutes, 1, 120);
                _breakLength = Math.Clamp(preferences.BreakMinutes, 1, 60);
                _cycleBaseSeconds = Math.Max(0, preferences.CycleBaseSeconds);
            }
            else { _focusLength = 25; _breakLength = 5; _cycleBaseSeconds = 0; }
        }
        catch { _focusLength = 25; _breakLength = 5; _cycleBaseSeconds = 0; }
    }

    private void SaveFocusPreferences()
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            var all = File.Exists(FocusSettingsPath)
                ? JsonSerializer.Deserialize<Dictionary<string, FocusPreference>>(File.ReadAllText(FocusSettingsPath)) ?? []
                : new Dictionary<string, FocusPreference>();
            all[FocusKey] = new FocusPreference
            {
                FocusMinutes = _focusLength, BreakMinutes = _breakLength, CycleBaseSeconds = _cycleBaseSeconds
            };
            File.WriteAllText(FocusSettingsPath, JsonSerializer.Serialize(all));
        }
        catch (Exception ex) { Notice("Não foi possível salvar as preferências do Pomodoro: " + ex.Message, true); }
    }

    private void ShowFocus()
    {
        if (_project is null) { Navigate("welcome"); return; }
        LoadFocusPreferences();
        Header("Tempo de foco", "Um passo de cada vez");
        var root = Stack(20);
        root.Children.Add(Label("Ajuste o ciclo, concentre-se e faça pausas. O tempo efetivo é guardado neste projeto.", 15, Muted));
        var duration = new NumericUpDown { Minimum = 1, Maximum = 120, Value = _focusLength, Increment = 1, MinWidth = 100 };
        var breakDuration = new NumericUpDown { Minimum = 1, Maximum = 60, Value = _breakLength, Increment = 1, MinWidth = 100 };
        duration.ValueChanged += (_, _) => { _focusLength = (int)(duration.Value ?? 25); SaveFocusPreferences(); RenderFocusClock(); };
        breakDuration.ValueChanged += (_, _) => { _breakLength = (int)(breakDuration.Value ?? 5); SaveFocusPreferences(); RenderFocusClock(); };
        var goal = new NumericUpDown { Minimum = 5, Maximum = 600, Value = ReadAppSettings().DailyGoalMinutes, Increment = 5, MinWidth = 120 };
        goal.ValueChanged += (_, _) => SaveAppSettings(new AppSettings { DailyGoalMinutes = (int)(goal.Value ?? 30) });
        var settings = Actions(FormField("Foco (min)", duration), FormField("Pausa (min)", breakDuration), FormField("Meta diária (min)", goal));
        root.Children.Add(Card(settings));
        _focusClock = Label("25:00", 66, Ink, FontWeight.Bold);
        _focusDescription = Label("Pronto para começar", 15, Muted);
        root.Children.Add(Card(StackWith(
            Label(_module is null ? _project.Title : _module.Title, 13, Teal, FontWeight.Bold),
            _focusClock, _focusDescription,
            Actions(ActionButton("Iniciar / retomar", async () => await StartFocusAsync()),
                ActionButton("Pausar", async () => await PauseFocusAsync(), false),
                ActionButton("Reiniciar", async () => await ResetFocusAsync(), false),
                ActionButton("Iniciar pausa", async () => await StartBreakAsync(), false)))));
        root.Children.Add(ActionButton("Voltar ao estudo", () => Navigate(_module is null ? "home" : "lesson"), false));
        Display(root);
        _ = LoadFocusAsync();
    }

    private async Task LoadFocusAsync()
    {
        if (_project is null) return;
        // Um cronômetro rodando em outro módulo/projeto é pausado e salvo antes de trocar o contexto,
        // senão o tempo após o último checkpoint se perderia ao fechar o aplicativo.
        if (_focus?.IsRunning == true && _focusProjectId is { } runningProject &&
            (runningProject != _project.Id || _focusModuleId != _module?.Id))
        {
            await _service.PauseFocusAsync(runningProject, _focusModuleId);
            Notice("O cronômetro anterior foi pausado e o tempo foi salvo.");
        }
        var result = await _service.GetFocusAsync(_project.Id, _module?.Id);
        if (!result.IsSuccess) { Notice(result.Error ?? "Não foi possível carregar o foco.", true); return; }
        _focus = result.Value;
        _focusProjectId = _project.Id;
        _focusModuleId = _module?.Id;
        if (_cycleBaseSeconds > _focus?.EffectiveSeconds)
        {
            _cycleBaseSeconds = _focus?.EffectiveSeconds ?? 0;
            SaveFocusPreferences();
        }
        RenderFocusClock();
    }

    private int FocusElapsedSeconds()
    {
        if (_focus is null) return 0;
        var elapsed = _focus.EffectiveSeconds;
        if (_focus.IsRunning && _focus.StartedAt is { } started)
            elapsed += Math.Max(0, (int)(DateTime.UtcNow - started).TotalSeconds);
        return Math.Max(0, elapsed - _cycleBaseSeconds);
    }

    private void RenderFocusClock()
    {
        if (_focusClock is null || _focusDescription is null || _page != "focus") return;
        var elapsed = _onBreak && _focusStartedUi is { } start
            ? Math.Max(0, (int)(DateTime.UtcNow - start).TotalSeconds)
            : FocusElapsedSeconds();
        var limit = (_onBreak ? _breakLength : _focusLength) * 60;
        var remaining = Math.Max(0, limit - elapsed);
        _focusClock.Text = $"{remaining / 60:00}:{remaining % 60:00}";
        _focusDescription.Text = _onBreak ? "Pausa em andamento" :
            _focus?.IsRunning == true ? "Sessão de foco em andamento" :
            elapsed > 0 ? $"Pausado · {elapsed / 60} min de foco efetivo" : "Pronto para começar";
        if (!_onBreak && _focus?.IsRunning == true && remaining == 0)
        {
            _ = PauseFocusAsync();
            CallAttention("Ciclo de foco concluído! Levante, beba água e inicie a pausa.");
        }
        if (_onBreak && remaining == 0)
        {
            _onBreak = false;
            _focusStartedUi = null;
            CallAttention("Pausa concluída. Pronto para mais um ciclo?");
        }
    }

    /// <summary>Traz a janela para frente e avisa o fim do ciclo, mesmo se o aluno estiver em outro programa.</summary>
    private void CallAttention(string message)
    {
        Notice(message);
        try
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Topmost = true;
            Activate();
            Topmost = false;
        }
        catch (Exception) { /* alguns ambientes não permitem trazer a janela para frente */ }
    }

    private async Task StartFocusAsync()
    {
        if (_project is null) return;
        _onBreak = false;
        await RunAsync(async () =>
        {
            if (FocusElapsedSeconds() >= _focusLength * 60)
            {
                _cycleBaseSeconds = _focus?.EffectiveSeconds ?? 0;
                SaveFocusPreferences();
            }
            var result = await _service.StartFocusAsync(_project.Id, _module?.Id);
            if (!result.IsSuccess) { Notice(result.Error ?? "Não foi possível iniciar o foco.", true); return; }
            _focus = result.Value;
            _focusProjectId = _project.Id;
            _focusModuleId = _module?.Id;
            RenderFocusClock();
        });
    }

    private async Task PauseFocusAsync()
    {
        if (_project is null || _focus?.IsRunning != true) return;
        await RunAsync(async () =>
        {
            // Pausa a sessão que está de fato rodando, mesmo que o aluno tenha mudado de módulo.
            var result = _focusProjectId is { } runningProject
                ? await _service.PauseFocusAsync(runningProject, _focusModuleId)
                : await _service.PauseFocusAsync(_project.Id, _module?.Id);
            if (!result.IsSuccess) { Notice(result.Error ?? "Não foi possível pausar o foco.", true); return; }
            _focus = result.Value;
            await RefreshProjectAsync();
            RenderFocusClock();
        });
    }

    private async Task ResetFocusAsync()
    {
        if (_project is null) return;
        if (_focus?.IsRunning == true) await PauseFocusAsync();
        _cycleBaseSeconds = _focus?.EffectiveSeconds ?? 0;
        _onBreak = false;
        SaveFocusPreferences();
        RenderFocusClock();
    }

    private async Task StartBreakAsync()
    {
        if (_focus?.IsRunning == true) await PauseFocusAsync();
        _onBreak = true;
        _focusStartedUi = DateTime.UtcNow;
        RenderFocusClock();
    }

    private async Task CheckpointFocusTickAsync()
    {
        if (_checkpointInFlight || _busy || _focus?.IsRunning != true || _focusProjectId is not { } projectId) return;
        _checkpointInFlight = true;
        try
        {
            var result = await _service.CheckpointFocusAsync(projectId, _focusModuleId);
            if (result.IsSuccess) _focus = result.Value;
            else if (_page == "focus") Notice(result.Error ?? "Não foi possível salvar o tempo de foco.", true);
        }
        catch (Exception ex)
        {
            if (_page == "focus") Notice("Não foi possível salvar o tempo de foco: " + ex.Message, true);
        }
        finally { _checkpointInFlight = false; }
    }

    private async Task CloseAfterCheckpointAsync()
    {
        try
        {
            while (_checkpointInFlight) await Task.Delay(50);
            if (_focusProjectId is { } projectId)
                await _service.CheckpointFocusAsync(projectId, _focusModuleId);
        }
        finally
        {
            _closingAfterCheckpoint = true;
            Close();
        }
    }
}
