using System.Drawing;

namespace DisplayToggle;

/// <summary>Minimal "Set peer…" form: host + port + optional shared secret (manual-IP pairing).</summary>
internal sealed class PeerDialog : Form
{
    private readonly TextBox _host = new() { Width = 200 };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Width = 80 };
    private readonly TextBox _secret = new() { Width = 200, UseSystemPasswordChar = true };

    public string PeerHost => _host.Text.Trim();
    public int PeerPort => (int)_port.Value;
    public string PeerSecret => _secret.Text;

    public PeerDialog(string host, int port, string secret)
    {
        Text = "Set peer";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(340, 190);

        _host.Text = host;
        _port.Value = Math.Clamp(port, 1, 65535);
        _secret.Text = secret;

        var hostLabel = new Label { Text = "Peer host / IP:", AutoSize = true, Location = new Point(16, 18) };
        _host.Location = new Point(120, 15);
        var portLabel = new Label { Text = "Port:", AutoSize = true, Location = new Point(16, 52) };
        _port.Location = new Point(120, 49);
        var secretLabel = new Label { Text = "Shared secret:", AutoSize = true, Location = new Point(16, 86) };
        _secret.Location = new Point(120, 83);
        var hint = new Label
        {
            Text = "Optional — set the same secret on both machines to authenticate the link.",
            AutoSize = false, Location = new Point(16, 110), Size = new Size(308, 30),
            ForeColor = SystemColors.GrayText,
        };

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(160, 150), Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(245, 150), Width = 75 };

        Controls.AddRange(new Control[] { hostLabel, _host, portLabel, _port, secretLabel, _secret, hint, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
