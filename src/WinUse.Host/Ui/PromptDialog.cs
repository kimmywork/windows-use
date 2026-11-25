using System.Drawing;
using System.Windows.Forms;

namespace WinUse.Host.Ui;

/// <summary>极简文本输入对话框（避免依赖 Microsoft.VisualBasic）。</summary>
internal sealed class PromptDialog : Form
{
    private readonly TextBox _input;

    private PromptDialog(string caption, string label, string defaultValue)
    {
        Text = caption;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(420, 132);
        Font = SystemFonts.MessageBoxFont;

        var lbl = new Label
        {
            Text = label,
            Location = new Point(12, 12),
            Size = new Size(396, 20),
        };

        _input = new TextBox
        {
            Text = defaultValue,
            Location = new Point(12, 36),
            Size = new Size(396, 24),
        };
        _input.SelectAll();

        var ok = new Button
        {
            Text = "确定",
            DialogResult = DialogResult.OK,
            Location = new Point(228, 84),
            Size = new Size(84, 28),
        };
        var cancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Location = new Point(324, 84),
            Size = new Size(84, 28),
        };

        Controls.AddRange(new Control[] { lbl, _input, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
    }

    /// <summary>显示输入框；取消返回 null。</summary>
    public static string? Show(IWin32Window? owner, string caption, string label, string defaultValue = "")
    {
        using var dlg = new PromptDialog(caption, label, defaultValue);
        var result = owner is null ? dlg.ShowDialog() : dlg.ShowDialog(owner);
        return result == DialogResult.OK ? dlg._input.Text.Trim() : null;
    }
}
