using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using MOGAController;

// The Blank Page item template is documented at http://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace JoystickTest
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
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
                connectBDA.Content = "Connect to BD&A";
                connectMoga2.Content = "Connect to Moga 2";
                connectMoga2.IsEnabled = connectBDA.IsEnabled = true;
            }
            else
            {
                controller.Connect(btn.Content.Equals("Connect to BD&A") ? "BD&A" : "Moga 2");
                (sender as Button).Content = "Disconnect";
                if (btn.Name.Equals("connectBDA")) connectMoga2.IsEnabled = false; else connectBDA.IsEnabled = false;
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
