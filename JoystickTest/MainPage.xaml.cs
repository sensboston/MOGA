using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using MOGAController;

namespace JoystickTest
{
    public sealed partial class MainPage : Page
    {
        Controller controller = new Controller();

        public MainPage()
        {
            this.InitializeComponent();

            controller.StateChanged += Controller_StateChanged;
            controller.KeyChanged += Controller_KeyChanged;
            controller.AxisChanged += Controller_AxisChanged;
        }

        private void Connect_Click(object sender, RoutedEventArgs e)
        {
            Button btn = (sender as Button);
            if (btn.Content.Equals("Disconnect"))
            {
                controller.Disconnect();
                keyStatus.Text = axisStatus.Text = "";
                (sender as Button).Content = "Connect to BD&A";
            }
            else
            {
                controller.Connect();
                (sender as Button).Content = "Disconnect";
            }
        }

        private void Controller_StateChanged(StateEvent param)
        {
            controllerStatus.Text = param.StateValue.ToString();
        }

        private void Controller_KeyChanged(KeyEvent param)
        {
            keyStatus.Text = "keycode: " + param.KeyCode.ToString() + "   action: " + param.Action.ToString();
        }

        private void Controller_AxisChanged(MotionEvent param)
        {
            axisStatus.Text = "axis: " + param.Axis.ToString() + "   value: " + param.AxisValue.ToString();
        }
    }
}
