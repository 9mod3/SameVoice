using System.Windows;
using System.Windows.Input;

namespace SameVoice.App;

public enum CloseChoice
{
    Cancel,
    Minimize,
    Exit
}

public partial class CloseChoiceDialog : Window
{
    private CloseChoice _choice = CloseChoice.Cancel;

    public CloseChoiceDialog(bool trayAvailable)
    {
        InitializeComponent();
        MessageText.Text = trayAvailable
            ? "要直接退出同声，还是让它继续驻留后台？"
            : "系统托盘当前不可用。要直接退出，还是把窗口最小化到任务栏继续播放？";
        HintText.Text = trayAvailable
            ? "最小化后，同声会继续在系统托盘运行；右键托盘图标可以选择「退出」完全结束。"
            : "最小化后，同声窗口会留在任务栏，播放/共享继续；停止后可从任务栏重新打开窗口。";
        MinimizeButton.Content = trayAvailable ? "最小化到系统托盘" : "最小化到任务栏";
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Cancel_Click(this, new RoutedEventArgs());
        };
    }

    public static CloseChoice Ask(Window owner, bool trayAvailable)
    {
        var dialog = new CloseChoiceDialog(trayAvailable) { Owner = owner };
        dialog.ShowDialog();
        return dialog._choice;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        _choice = CloseChoice.Minimize;
        Close();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        _choice = CloseChoice.Exit;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _choice = CloseChoice.Cancel;
        Close();
    }
}
