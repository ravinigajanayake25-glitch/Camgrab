using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using VideoOS.Platform;
using VideoOS.Platform.Login;
using VideoOS.Platform.SDK.UI.LoginDialog;

namespace MediaLiveViewer
{
	static class Program
	{
        private static readonly Guid IntegrationId = new Guid("14FA2FED-87D2-44E2-88B6-23A21551BD0D");
        private const string IntegrationName = "Media Live Viewer";
        private const string Version = "1.0";
        private const string ManufacturerName = "Sample Manufacturer";

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
		static void Main()
		{
			Application.EnableVisualStyles();
			Application.SetCompatibleTextRenderingDefault(false);

			Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
			AppDomain.CurrentDomain.UnhandledException += (s, e) =>
			{
				var ex = e.ExceptionObject as Exception;
				MessageBox.Show(
					$"A server connection error or unhandled exception occurred:\n\n{ex?.Message}\n\nType: {ex?.GetType().FullName}\n\nDetails:\n{ex?.ToString()}",
					IntegrationName,
					MessageBoxButtons.OK,
					MessageBoxIcon.Error);
			};
			Application.ThreadException += (s, e) =>
			{
				MessageBox.Show(
					$"A thread error occurred:\n\n{e.Exception.Message}\n\nDetails:\n{e.Exception}",
					IntegrationName,
					MessageBoxButtons.OK,
					MessageBoxIcon.Error);
			};

			try
			{
				System.Net.ServicePointManager.SecurityProtocol |=
					System.Net.SecurityProtocolType.Tls12 |
					System.Net.SecurityProtocolType.Tls11 |
					System.Net.SecurityProtocolType.Tls;
				System.Net.ServicePointManager.ServerCertificateValidationCallback =
					(sender, certificate, chain, sslPolicyErrors) => true;
			}
			catch { }

			VideoOS.Platform.SDK.Environment.Initialize();		// Initialize the standalone Environment
			VideoOS.Platform.SDK.UI.Environment.Initialize();	// Initialize login and item-picker UI
			VideoOS.Platform.SDK.Media.Environment.Initialize();        // Initialize the standalone Environment

			EnvironmentManager.Instance.EnvironmentOptions[EnvironmentOptions.HardwareDecodingMode] = "Off";
			EnvironmentManager.Instance.TraceFunctionCalls = false;

			while (true)
			{
				Connected = false;
				DialogLoginForm loginForm = new DialogLoginForm(SetLoginResult, IntegrationId, IntegrationName, Version, ManufacturerName);
				Application.Run(loginForm);

				if (Connected)
				{
					MessageBox.Show("Login succeeded. The server connection is verified; loading camera information.",
						IntegrationName, MessageBoxButtons.OK, MessageBoxIcon.Information);
					Application.Run(new CameraInventoryForm());
					break;
				}

				DialogResult retry = MessageBox.Show(
					"A successful connection was not confirmed. The dialog may have been cancelled, or the server address, authentication type, username, or password may be incorrect. Check the details and try again?",
					IntegrationName,
					MessageBoxButtons.YesNo,
					MessageBoxIcon.Warning);
				if (retry != DialogResult.Yes)
				{
					break;
				}
			}

			try
			{
				VideoOS.Platform.SDK.Environment.RemoveAllServers();
			}
			catch { }
			Environment.Exit(0);
		}

		private static bool Connected = false;
		private static void SetLoginResult(bool connected)
		{
			Connected = connected;
		}
	}
}
