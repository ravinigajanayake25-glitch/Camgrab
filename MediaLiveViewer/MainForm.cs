using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using VideoOS.Platform;
using VideoOS.Platform.Data;
using VideoOS.Platform.Live;
using VideoOS.Platform.Messaging;
using VideoOS.Platform.UI;
using VideoOS.Platform.Util.AdaptiveStreaming;

namespace MediaLiveViewer
{
	public partial class MainForm : Form
	{
		#region private fields

		private Item _selectItem1;
		private JPEGLiveSource _jpegLiveSource;

		private int _count = 0;
		private CancellationTokenSource _alarmCts;
		private Button _btnAlarm;

		#endregion

		#region construction and close

		public MainForm(Item initialCamera = null)
		{
			InitializeComponent();
			comboBoxResolution.SelectedIndex = 1;
			comboBoxCompression.SelectedIndex = 0;
			comboBoxStreamSelection.SelectedIndex = 0;

			_btnAlarm = new Button
			{
				Text = "🚨 Trigger Alarm",
				Size = new Size(135, 23),
				Location = new Point(buttonOK.Location.X - 145, buttonOK.Location.Y),
				Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
				BackColor = Color.FromArgb(200, 35, 51),
				ForeColor = Color.White,
				FlatStyle = FlatStyle.Flat,
				Font = new Font(Font.FontFamily, 8.5f, FontStyle.Bold),
				Cursor = Cursors.Hand
			};
			_btnAlarm.FlatAppearance.BorderSize = 0;
			_btnAlarm.Click += (s, e) => ToggleAlarmSound();
			Controls.Add(_btnAlarm);

			if (initialCamera != null)
			{
				Shown += (s, e) => PlayCamera(initialCamera);
			}
		}

		private void OnClose(object sender, EventArgs e)
		{
			if (_alarmCts != null)
			{
				_alarmCts.Cancel();
				_alarmCts.Dispose();
				_alarmCts = null;
			}
			if (_jpegLiveSource != null)
				_jpegLiveSource.Close();
			Close();
		}

		private void ToggleAlarmSound()
		{
			if (_alarmCts != null)
			{
				_alarmCts.Cancel();
				_alarmCts.Dispose();
				_alarmCts = null;
				_btnAlarm.Text = "🚨 Trigger Alarm";
				_btnAlarm.BackColor = Color.FromArgb(200, 35, 51);
				return;
			}

			TriggerMilestoneAlarm(_selectItem1, out string details);

			_alarmCts = new CancellationTokenSource();
			var token = _alarmCts.Token;
			_btnAlarm.Text = "⏹ Stop Alarm";
			_btnAlarm.BackColor = Color.FromArgb(230, 80, 0);

			Task.Run(async () =>
			{
				try
				{
					var sw = Stopwatch.StartNew();
					while (!token.IsCancellationRequested && sw.Elapsed < TimeSpan.FromSeconds(30))
					{
						System.Media.SystemSounds.Exclamation.Play();
						try { Console.Beep(1200, 200); } catch { }
						if (token.IsCancellationRequested) break;
						try { Console.Beep(800, 200); } catch { }
						if (token.IsCancellationRequested) break;
						await Task.Delay(50, token);
					}
				}
				catch (OperationCanceledException) { }
				catch { }
				finally
				{
					if (IsHandleCreated && !IsDisposed)
					{
						BeginInvoke((Action)(() =>
						{
							if (_alarmCts != null)
							{
								_alarmCts.Dispose();
								_alarmCts = null;
								_btnAlarm.Text = "🚨 Trigger Alarm";
								_btnAlarm.BackColor = Color.FromArgb(200, 35, 51);
							}
						}));
					}
				}
			}, token);
		}

		private bool TriggerMilestoneAlarm(Item camera, out string details)
		{
			try
			{
				var cameraName = camera?.Name ?? "CamGrab System";
				var cameraFqid = camera?.FQID ?? EnvironmentManager.Instance.MasterSite;

				var eventSource = new EventSource
				{
					FQID = cameraFqid,
					Name = cameraName
				};

				var eventHeader = new EventHeader
				{
					ID = Guid.NewGuid(),
					Class = "Alarm",
					Type = "Manual Alarm",
					Timestamp = DateTime.UtcNow,
					Message = $"🚨 Manual Alarm triggered for {cameraName} from MediaLiveViewer",
					Name = "Manual Alarm",
					Source = eventSource,
					Priority = 1,
					PriorityName = "High"
				};

				var alarm = new Alarm
				{
					EventHeader = eventHeader,
					State = 1,
					StateName = "New"
				};

				try
				{
					EnvironmentManager.Instance.SendMessage(
						new VideoOS.Platform.Messaging.Message(MessageId.Server.NewAlarmCommand) { Data = alarm }
					);
				}
				catch { }

				try
				{
					var baseEvent = new BaseEvent { EventHeader = eventHeader };
					EnvironmentManager.Instance.SendMessage(
						new VideoOS.Platform.Messaging.Message(MessageId.Server.NewEventCommand) { Data = baseEvent }
					);
				}
				catch { }

				try
				{
					var serverId = EnvironmentManager.Instance.MasterSite?.ServerId;
					if (serverId != null)
					{
						MessageCommunicationManager.Start(serverId);
						var msgCom = MessageCommunicationManager.Get(serverId);
						if (msgCom != null)
						{
							var alarmMsg = new VideoOS.Platform.Messaging.Message(MessageId.Server.NewAlarmCommand, alarm);
							msgCom.TransmitMessage(alarmMsg, null, null, null);

							var baseEvent = new BaseEvent { EventHeader = eventHeader };
							var eventMsg = new VideoOS.Platform.Messaging.Message(MessageId.Server.NewEventCommand, baseEvent);
							msgCom.TransmitMessage(eventMsg, null, null, null);
						}
					}
				}
				catch { }

				details = $"Milestone Alarm sent for {cameraName}";
				return true;
			}
			catch (Exception ex)
			{
				details = "Milestone error: " + ex.Message;
				return false;
			}
		}
		#endregion


		#region Live Click handling 
		public void PlayCamera(Item camera)
		{
			if (_jpegLiveSource != null)
			{
				// Close any current displayed JPEG Live Source
				_jpegLiveSource.LiveContentEvent -= JpegLiveSource1LiveNotificationEvent;
				_jpegLiveSource.LiveStatusEvent -= JpegLiveStatusNotificationEvent;
				_jpegLiveSource.Close();
				_jpegLiveSource = null;
				pictureBox1.Image = new Bitmap(1, 1);
			}

			ClearAllFlags();
			ResetSelections();

			_selectItem1 = camera;
			if (_selectItem1 == null)
			{
				buttonSelect1.Text = "Select Camera ...";
				labelCount.Text = "0";
				labelSize.Text = "";
				labelResolution.Text = "";
				buttonPause.Enabled = false;
				checkBoxAspect.Enabled = true;
				checkBoxFill.Enabled = true;
				checkBoxKeyFramesOnly.Enabled = true;
				buttonLift.Enabled = false;
				return;
			}

			buttonSelect1.Text = _selectItem1.Name;

			_jpegLiveSource = new JPEGLiveSource(_selectItem1);
			try
			{
				SetResolution();
				_jpegLiveSource.LiveModeStart = true;
				_jpegLiveSource.SetKeepAspectRatio(checkBoxAspect.Checked, checkBoxFill.Checked);
				checkBoxAspect.Enabled = false;
				checkBoxFill.Enabled = false;
				checkBoxKeyFramesOnly.Enabled = false;
				comboBoxResolution.Enabled = !checkBoxAspect.Checked;   // Only allow resolution change, if filling available space
				_jpegLiveSource.Width = pictureBox1.Width;
				_jpegLiveSource.Height = pictureBox1.Height;
				_jpegLiveSource.KeyFramesOnly = checkBoxKeyFramesOnly.Checked;
				SetStreamType(pictureBox1.Width, pictureBox1.Height);
				_jpegLiveSource.Init();
				_jpegLiveSource.LiveContentEvent += JpegLiveSource1LiveNotificationEvent;
				_jpegLiveSource.LiveStatusEvent += JpegLiveStatusNotificationEvent;

				labelCount.Text = "0";
				buttonPause.Enabled = true;
				buttonLift.Enabled = true;
				_count = 0;

			}
			catch (Exception ex)
			{
				MessageBox.Show("Could not Init:" + ex.Message);
				_jpegLiveSource = null;
			}
		}

		private void OnSelect1Click(object sender, EventArgs e)
		{
			ItemPickerWpfWindow itemPicker = new ItemPickerWpfWindow()
			{
				KindsFilter = new List<Guid> { Kind.Camera },
				SelectionMode = SelectionModeOptions.AutoCloseOnSelect,
				Items = Configuration.Instance.GetItems(),
			};

			if (itemPicker.ShowDialog().Value)
			{
				PlayCamera(itemPicker.SelectedItems.First());
			}
			else
			{
				PlayCamera(null);
			}
		}

	    private bool OnMainThread = false;
		/// <summary>
		/// This event is called when JPEG is available or some exception has occurred
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		void JpegLiveSource1LiveNotificationEvent(object sender, EventArgs e)
		{
			if (this.InvokeRequired)
			{
			    if (OnMainThread)
			    {
                    LiveContentEventArgs args = e as LiveContentEventArgs;
			        if (args != null && args.LiveContent != null)
			        {
                        // UI thread is too busy - discard this frame from display
			            args.LiveContent.Dispose();
			        }
			        return;
			    }
			    OnMainThread = true;
			    // Make sure we execute on the UI thread before updating UI Controls
				BeginInvoke(new EventHandler(JpegLiveSource1LiveNotificationEvent), new[] { sender, e });
			}
			else
			{
				LiveContentEventArgs args = e as LiveContentEventArgs;
				if (args != null)
				{
					if (args.LiveContent != null)		
					{
						// Display the received JPEG
						labelSize.Text = ""+args.LiveContent.Content.Length;

						int width = args.LiveContent.Width;
						int height = args.LiveContent.Height;

						MemoryStream ms = new MemoryStream(args.LiveContent.Content);
						Bitmap newBitmap = new Bitmap(ms);

						// Render Server Time Ribbon in Top-Left Corner
						try
						{
							using (var g = Graphics.FromImage(newBitmap))
							{
								g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
								g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

								string camName = _selectItem1?.Name ?? "Camera";
								var now = DateTime.Now;
								string dateStr = now.ToString("yyyy-MM-dd");
								string timeStr = now.ToString("HH:mm:ss");

								float baseFontSize = Math.Max(10.5f, Math.Min(22f, height * 0.032f));
								float badgeFontSize = Math.Max(9f, baseFontSize * 0.82f);

								using (var mainFont = new Font("Segoe UI", baseFontSize, FontStyle.Bold))
								using (var timeFont = new Font("Consolas", baseFontSize * 1.05f, FontStyle.Bold))
								using (var badgeFont = new Font("Segoe UI", badgeFontSize, FontStyle.Bold))
								using (var ribbonBg = new SolidBrush(Color.FromArgb(220, 15, 23, 42)))
								using (var badgeBg = new SolidBrush(Color.FromArgb(240, 2, 132, 199)))
								using (var accentLine = new SolidBrush(Color.FromArgb(56, 189, 248)))
								using (var whiteText = new SolidBrush(Color.White))
								using (var yellowTime = new SolidBrush(Color.FromArgb(254, 240, 138)))
								using (var dateText = new SolidBrush(Color.FromArgb(226, 232, 240)))
								using (var dotBrush = new SolidBrush(Color.FromArgb(34, 197, 94)))
								{
									string badgeLabel = "TIME";
									var badgeSize = g.MeasureString(badgeLabel, badgeFont);
									var camSize = g.MeasureString(camName, mainFont);
									var dateSize = g.MeasureString(dateStr, mainFont);
									var timeSize = g.MeasureString(timeStr, timeFont);

									int padX = (int)(baseFontSize * 0.75f);
									int ribbonHeight = (int)(baseFontSize * 2.3f);
									int badgeWidth = (int)badgeSize.Width + padX * 2;
									int badgeHeight = ribbonHeight - 6;

									int contentWidth = (int)(badgeWidth + camSize.Width + dateSize.Width + timeSize.Width + padX * 5);
									int chevronCut = (int)(baseFontSize * 1.2f);
									int ribbonWidth = contentWidth + chevronCut + padX;

									var ribbonPoints = new Point[]
									{
										new Point(0, 0),
										new Point(ribbonWidth, 0),
										new Point(ribbonWidth - chevronCut, ribbonHeight),
										new Point(0, ribbonHeight)
									};
									g.FillPolygon(ribbonBg, ribbonPoints);

									using (var pen = new Pen(accentLine, Math.Max(2, (int)(baseFontSize * 0.15f))))
									{
										g.DrawLine(pen, 0, ribbonHeight, ribbonWidth - chevronCut, ribbonHeight);
									}

									int curX = padX;
									int badgeY = (ribbonHeight - badgeHeight) / 2;
									g.FillRectangle(badgeBg, curX, badgeY, badgeWidth, badgeHeight);

									int dotSize = (int)(badgeFontSize * 0.6f);
									int dotY = badgeY + (badgeHeight - dotSize) / 2;
									g.FillEllipse(dotBrush, curX + (int)(padX * 0.55f), dotY, dotSize, dotSize);

									float badgeTextX = curX + (int)(padX * 0.55f) + dotSize + (int)(baseFontSize * 0.35f);
									float badgeTextY = badgeY + (badgeHeight - badgeSize.Height) / 2f;
									g.DrawString(badgeLabel, badgeFont, whiteText, badgeTextX, badgeTextY);

									curX += badgeWidth + padX;

									float textY = (ribbonHeight - camSize.Height) / 2f;
									g.DrawString(camName, mainFont, whiteText, curX, textY);
									curX += (int)camSize.Width + (int)(padX * 0.7f);

									using (var sepBrush = new SolidBrush(Color.FromArgb(120, 148, 163, 184)))
									{
										g.DrawString("|", mainFont, sepBrush, curX, textY);
									}
									curX += (int)(padX * 0.7f);

									g.DrawString(dateStr, mainFont, dateText, curX, textY);
									curX += (int)dateSize.Width + (int)(padX * 0.7f);

									float timeY = (ribbonHeight - timeSize.Height) / 2f;
									g.DrawString(timeStr, timeFont, yellowTime, curX, timeY);
								}
							}
						}
						catch { }

						labelResolution.Text = "" + width + "x" + height;
						if (pictureBox1.Size.Width != 0 && pictureBox1.Size.Height != 0)
						{
							if (!checkBoxAspect.Checked && (newBitmap.Width != pictureBox1.Width || newBitmap.Height != pictureBox1.Height))
							{
								pictureBox1.Image = new Bitmap(newBitmap, pictureBox1.Size);
							}
							else
							{
								pictureBox1.Image = newBitmap;
							}
						}
						if (args.LiveContent.CroppingDefined)
						{
							labelCropRect.Text = "" + args.LiveContent.CropWidth + "x" + args.LiveContent.CropHeight;
						} else
						{
							labelCropRect.Text = "--";
						}
						textBoxDecodingStatus.Text = args.LiveContent.HardwareDecodingStatus;

						ms.Close();
						ms.Dispose();

						_count++;
						labelCount.Text = "" + _count;

						args.LiveContent.Dispose();
					} else if (args.Exception != null)
					{
						// Handle any exceptions occurred inside toolkit or on the communication to the VMS

					    Bitmap bitmap = new Bitmap(320, 240);
                        Graphics g = Graphics.FromImage(bitmap);
                        g.FillRectangle(Brushes.Black, 0, 0, bitmap.Width, bitmap.Height);
                        g.DrawString("Connection lost to server ...", new Font(FontFamily.GenericMonospace, 12), Brushes.White, new PointF(20, pictureBox1.Height/2 - 20));
					    g.Dispose();
                        pictureBox1.Image = new Bitmap(bitmap, pictureBox1.Size);
					    bitmap.Dispose();
					}

				}
                OnMainThread = false;
			}
		}

		/// <summary>
		/// This event is called when a Live status package has been received.
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		void JpegLiveStatusNotificationEvent(object sender, EventArgs e)
		{
			if (this.InvokeRequired)
			{
				BeginInvoke(new EventHandler(JpegLiveStatusNotificationEvent), new[] { sender, e });
			}
			else
			{
				LiveStatusEventArgs args = e as LiveStatusEventArgs;
				if (args != null)
				{
					if ((args.ChangedStatusFlags & StatusFlags.Motion) != 0)
						checkBoxMotion.Checked = (args.CurrentStatusFlags & StatusFlags.Motion) != 0;

					if ((args.ChangedStatusFlags & StatusFlags.Notification) != 0)
						checkBoxNotification.Checked = (args.CurrentStatusFlags & StatusFlags.Notification) != 0;

					if ((args.ChangedStatusFlags & StatusFlags.CameraConnectionLost) != 0)
						checkBoxOffline.Checked = (args.CurrentStatusFlags & StatusFlags.CameraConnectionLost) != 0;

					if ((args.ChangedStatusFlags & StatusFlags.Recording) != 0)
						checkBoxRec.Checked = (args.CurrentStatusFlags & StatusFlags.Recording) != 0;

					if ((args.ChangedStatusFlags & StatusFlags.LiveFeed) != 0)
						checkBoxLiveFeed.Checked = (args.CurrentStatusFlags & StatusFlags.LiveFeed) != 0;

					if ((args.ChangedStatusFlags & StatusFlags.ClientLiveStopped) != 0)
						checkBoxClientLive.Checked = (args.CurrentStatusFlags & StatusFlags.ClientLiveStopped) != 0;

					if ((args.ChangedStatusFlags & StatusFlags.DatabaseFail) != 0)
						checkBoxDBFail.Checked = (args.CurrentStatusFlags & StatusFlags.DatabaseFail) != 0;

					if ((args.ChangedStatusFlags & StatusFlags.DiskFull) != 0)
						checkBoxDiskFull.Checked = (args.CurrentStatusFlags & StatusFlags.DiskFull) != 0;

					Debug.WriteLine("LiveStatus: motion=" + checkBoxMotion.Checked + ", Notification=" + checkBoxNotification.Checked +
					                ", Offline=" + checkBoxOffline.Checked + ", Recording=" + checkBoxRec.Checked);

					if (checkBoxLiveFeed.Checked==false)
					{
						ClearAllFlags();
					}
				}
			}
		}

		private void ClearAllFlags()
		{
			checkBoxMotion.Checked = false;
			checkBoxNotification.Checked = false;
			//checkBoxOffline.Checked = false;
			checkBoxRec.Checked = false;
			checkBoxClientLive.Checked = false;
			checkBoxDBFail.Checked = false;
			checkBoxDiskFull.Checked = false;
			checkBoxLiveFeed.Checked = false;
		}

		private void ResetSelections()
		{
			comboBoxResolution.SelectedIndex = 1;
			comboBoxCompression.SelectedIndex = 0;
			checkBoxClientLive.Checked = false;
			buttonPause.Enabled = false;
			buttonPause.Text = "Pause";
		}

		private void OnResolutionChanged(object sender, EventArgs e)
		{
			if (_jpegLiveSource != null)
			{
				// _jpegLiveSource.LiveModeStart = false;
				SetResolution();
				// _jpegLiveSource.LiveModeStart = true;
			}
		}

		private void SetResolution()
		{
            int width = 0, height = 0;
			switch (comboBoxResolution.SelectedIndex)
			{
				case 0:
					width = 160;
					height = 120;
					break;
				case 1:
					width = 320;
					height = 240;
					break;
				case 2:
					width = 640;
					height = 480;
					break;
				case 3:
					width = 1024;
					height = 780;
					break;
                case 4:
                    width = 1920;
                    height = 1080;
                    break;
            }
            _jpegLiveSource.Width = width;
            _jpegLiveSource.Height = height;
            _jpegLiveSource.SetWidthHeight();

            SetStreamType(width, height);
        }

        private void SetStreamType(int width, int height)
        {
            if (null == _jpegLiveSource)
                return;

            switch (comboBoxStreamSelection.SelectedIndex)
            {
                case 0:
                    _jpegLiveSource.StreamSelectionParams.StreamSelectionType = StreamSelectionType.DefaultStream;
                    break;

                case 1:
                    _jpegLiveSource.StreamSelectionParams.StreamSelectionType = StreamSelectionType.MaximalResolution;
                    break;

                case 2:
                    _jpegLiveSource.StreamSelectionParams.SetStreamAdaptiveResolution(width, height);
                    _jpegLiveSource.StreamSelectionParams.StreamSelectionType = StreamSelectionType.AdaptiveToResolution;
                    break;
            }
        }

        #endregion

        private void OnClick(object sender, EventArgs e)
		{
			if (_jpegLiveSource != null)
			{
				if (_jpegLiveSource.LiveModeStart)				//TODO review when tool kit return msg is OK.
				{
					_jpegLiveSource.LiveModeStart = false;
					buttonPause.Text = "Start";
				}
				else
				{
					_jpegLiveSource.LiveModeStart = true;
					buttonPause.Text = "Pause";
				}
			}
		}

        private void OnQualityChanged(object sender, EventArgs e)
		{
			if (_jpegLiveSource!=null)
			{
				int newQuality;
                if (Int32.TryParse(comboBoxCompression.SelectedItem.ToString(), out newQuality))
                    _jpegLiveSource.Compression = newQuality;
			}
		}

		private void OnCropChanged(object sender, EventArgs e)
		{
			if (_jpegLiveSource!=null)
			{
				if (checkBoxCrop.Checked)
				{
					_jpegLiveSource.Width = 80;
					_jpegLiveSource.Height = 80;
					_jpegLiveSource.SetWidthHeight();
					_jpegLiveSource.SetCropRectangle(0.5, 0.5, 0.5, 0.5);
				}
				else
				{
					SetResolution();
					_jpegLiveSource.SetCropRectangle(0.0, 0.0, 1.0, 1.0);
				}
			}
		}

		private void OnResizePictureBox(object sender, EventArgs e)
		{
		    if (_jpegLiveSource != null)
		    {
		        _jpegLiveSource.Width = pictureBox1.Width;
		        _jpegLiveSource.Height = pictureBox1.Height;
		        _jpegLiveSource.SetWidthHeight();
		    }

            SetStreamType(pictureBox1.Width, pictureBox1.Height);
        }

        private void comboBoxfps_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_jpegLiveSource != null)
            {
                var value = comboBoxfps.SelectedItem.ToString();
                if (value.Equals("default", StringComparison.InvariantCultureIgnoreCase))
                {
                    _jpegLiveSource.FPS = 0;
                }
                else
                {
                    int newFps;
                    if (Int32.TryParse(comboBoxfps.SelectedItem.ToString(), out newFps))
                        _jpegLiveSource.FPS = newFps;
                }
            }
        }

        private void buttonLift_Click(object sender, EventArgs e)
        {
            Configuration.Instance.ServerFQID.ServerId.UserContext.SetPrivacyMaskLifted(!Configuration.Instance.ServerFQID.ServerId.UserContext.PrivacyMaskLifted);
        }

        private void comboBoxStreamSelection_SelectedIndexChanged(object sender, EventArgs e)
        {
            SetStreamType(pictureBox1.Width, pictureBox1.Height);
        }
    }
}
