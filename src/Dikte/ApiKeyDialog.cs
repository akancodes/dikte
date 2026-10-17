using Dikte.Core;

namespace Dikte;

/// <summary>Asks for the API key and tries it before saving.</summary>
sealed class ApiKeyDialog : Form
{
    readonly TextBox _key = new() { UseSystemPasswordChar = true, Dock = DockStyle.Top };
    readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Top, Height = 40, ForeColor = Color.DimGray };
    readonly Button _save = new() { Text = "Save", AutoSize = true };
    readonly Transcriber _api;
    readonly Settings _settings;

    public ApiKeyDialog(Transcriber api, Settings settings)
    {
        _api = api;
        _settings = settings;
        Text = "Dikte · API key";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(440, 170);
        Padding = new Padding(16);
        Font = new Font("Segoe UI", 9.5f);

        var intro = new Label
        {
            Text = $"Paste an API key for {new Uri(settings.ApiBase).Host}.\nIt is stored encrypted for your Windows user only.",
            Dock = DockStyle.Top,
            Height = 48,
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40 };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_save);

        Controls.Add(_status);
        Controls.Add(_key);
        Controls.Add(intro);
        Controls.Add(buttons);
        AcceptButton = _save;
        CancelButton = cancel;
        _save.Click += async (_, _) => await SaveAsync();
    }

    async Task SaveAsync()
    {
        var key = _key.Text.Trim();
        if (key.Length == 0) return;
        _save.Enabled = false;
        _status.ForeColor = Color.DimGray;
        _status.Text = "Checking the key…";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string? problem;
        try { problem = await _api.CheckKeyAsync(_settings, key, cts.Token); }
        catch (OperationCanceledException) { problem = "The API did not answer in time."; }
        if (problem is null)
        {
            KeyStore.Save(key);
            DialogResult = DialogResult.OK;
            Close();
            return;
        }
        _status.ForeColor = Color.Firebrick;
        _status.Text = problem;
        _save.Enabled = true;
    }
}
