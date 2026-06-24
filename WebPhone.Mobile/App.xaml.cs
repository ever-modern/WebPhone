using WebPhone.Background;

namespace WebPhone.Mobile
{
    public partial class App : Application
    {
        private readonly AppStarter _appStarter;

        public App(AppStarter appStarter)
        {
            _appStarter = appStarter;
            InitializeComponent();

            _ = Task.Run(() => _appStarter.EnsureStartedAsync());
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            return new Window(new MainPage()) { Title = "WebPhone.Android" };
        }
    }
}
