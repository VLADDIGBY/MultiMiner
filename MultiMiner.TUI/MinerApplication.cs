﻿using MultiMiner.UX.Data;
using MultiMiner.UX.Extensions;
using MultiMiner.UX.ViewModels;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace MultiMiner.TUI
{
    class MinerApplication : ConsoleApplication
    {
        enum Screen
        {
            Main,
            Repl
        }

        //upper-case chars serve as a command alias, e.g. Quit = q
        private const string QuitCommand = "Quit";
        private const string StartCommand = "start";
        private const string StopCommand = "stop";
        private const string RestartCommand = "restart";
        private const string ScanCommand = "scan";
        private const string SwitchAllCommand = "SwitchAll";
        private const string PoolCommand = "Pool";
        private const string AddVerb = "Add";
        private const string RemoveVerb = "Remove";
        private const string ListVerb = "List";
        private const string ScreenCommand = "SCreen";

        private const string Ellipsis = "..";

        private readonly ApplicationViewModel app = new ApplicationViewModel();
        private readonly ISynchronizeInvoke threadContext = new SimpleSyncObject();
        private readonly List<NotificationEventArgs> notifications = new List<NotificationEventArgs>();
        private readonly List<string> replBuffer = new List<string>();

        private readonly bool isWindows = Utility.OS.OSVersionPlatform.GetGenericPlatform() != PlatformID.Unix;
        private readonly bool isLinux = Utility.OS.OSVersionPlatform.GetConcretePlatform() == PlatformID.Unix;
        private readonly bool isMac = Utility.OS.OSVersionPlatform.GetConcretePlatform() == PlatformID.MacOSX;

        private string currentProgress = String.Empty;
        private PromptEventArgs currentPrompt;
        private DateTime promptTime;
        private Screen currentScreen = Screen.Main;
        private string incomeSummaryText;

        #region ConsoleApplication overrides
        protected override void SetupApplication()
        {
            Console.CursorVisible = false;

            app.DataModified += (object sender, EventArgs e) =>
            {
                ScreenDirty = true;
            };

            app.ConfigurationModified += (object sender, EventArgs e) =>
            {
                ScreenDirty = true;
            };

            app.ProgressStarted += (object sender, ProgressEventArgs e) =>
            {
                currentProgress = e.Text;
                RenderScreen();
            };

            app.ProgressCompleted += (object sender, EventArgs e) =>
            {
                currentProgress = String.Empty;
                RenderScreen();
            };

            app.NotificationReceived += (object sender, NotificationEventArgs e) =>
            {
                notifications.Add(e);
                ScreenDirty = true;
            };

            app.NotificationDismissed += (object sender, NotificationEventArgs e) =>
            {
                notifications.RemoveAll(n => !String.IsNullOrEmpty(n.Id) && n.Id.Equals(e.Id));
                ScreenDirty = true;
            };

            app.PromptReceived += (object sender, PromptEventArgs e) =>
            {
                currentPrompt = e;
                promptTime = DateTime.Now;
                RenderScreen();
            };

            app.Context = threadContext;
        }

        protected override void LoadSettings()
        {
            app.ApplicationConfiguration.LoadApplicationConfiguration(app.PathConfiguration.SharedConfigPath);
            app.EngineConfiguration.LoadStrategyConfiguration(app.PathConfiguration.SharedConfigPath); //needed before refreshing coins
            app.EngineConfiguration.LoadCoinConfigurations(app.PathConfiguration.SharedConfigPath); //needed before refreshing coins
            app.LoadSettings();
        }

        protected override void StartupApplication()
        {
            //kill known owned processes to release inherited socket handles
            if (ApplicationViewModel.KillOwnedProcesses())
                //otherwise may still be prompted below by check for disowned miners
                System.Threading.Thread.Sleep(500);

            //check for disowned miners before refreshing devices
            if (app.ApplicationConfiguration.DetectDisownedMiners)
                app.CheckForDisownedMiners();

            app.SetupCoinApi(); //so we target the correct API
            app.RefreshCoinStats();

            app.SetupCoalescedTimers();
            app.UpdateBackendMinerAvailability();
            app.CheckAndDownloadMiners();
            app.SetupRemoting();
            app.SetupNetworkDeviceDetection();
            app.CheckForUpdates();
            app.SetupMiningOnStartup();
        }

        protected override void TearDownApplication()
        {
            app.StopMiningLocally();
            app.DisableRemoting();
            app.Context = null;
        }
        
        protected override void SaveSettings()
        {
            app.EngineConfiguration.SaveAllConfigurations();
            app.ApplicationConfiguration.SaveApplicationConfiguration();
        }

        protected override void RenderScreen()
        {
#if DEBUG
            //OutputJunk();
#endif

            if (currentScreen == Screen.Repl)
                RenderReplScreen();
            else
                RenderMainScreen();
        }
        
        protected override void RenderInput()
        {
            if (currentScreen == Screen.Repl)
                OutputInput(Console.WindowWidth);
            else
                OutputInput(Console.WindowWidth - incomeSummaryText.Length);
        }

        private void RenderReplScreen()
        {
            OutputReplBuffer();

            OutputInput(Console.WindowWidth);
        }

        private void OutputReplBuffer()
        {
            var lines = replBuffer.ToList();
            lines.Reverse();
            lines = lines.Take(Console.WindowHeight - 1).ToList();

            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];

                if (SetCursorPosition(0, i))
                    WriteText(line.PadFitRight(Console.WindowWidth, Ellipsis));
            }

            for (int i = lines.Count; i < Console.WindowHeight - 1; i++)
                ClearRow(i);
        }

        private void RenderMainScreen()
        {
            OutputDevices();

            OutputSpecial();

            OutputNotifications();

            OutputStatus();

            incomeSummaryText = OutputIncome();

            //[ERROR] FATAL UNHANDLED EXCEPTION: System.NotImplementedException: The requested feature is not implemented.
            if (isWindows) FillLastCell();

            OutputInput(Console.WindowWidth - incomeSummaryText.Length);
        }

        protected override bool HandleCommandInput(string input)
        {
            if (InputMatchesCommand(input, QuitCommand))
                Quit();
            else if (InputMatchesCommand(input, StartCommand))
                app.StartMining();
            else if (InputMatchesCommand(input, StopCommand))
                app.StopMining();
            else if (InputMatchesCommand(input, RestartCommand))
                app.RestartMining();
            else if (InputMatchesCommand(input, ScanCommand))
                app.ScanHardwareLocally();
            else if (InputMatchesCommand(input, SwitchAllCommand))
                HandleSwitchAllCommand(input);
            else if (InputMatchesCommand(input, PoolCommand))
                HandlePoolCommand(input);
            else if (InputMatchesCommand(input, ScreenCommand))
                HandleScreenCommand(input);
            else
            {
                AddNotification(String.Format("Unknown command: {0}", input.Split(' ').First()));
                return false; //exit early
            }

            //successful command
            return true;
        }
        #endregion

        private void OutputJunk()
        {
            for (int row = 0; row < Console.WindowHeight; row++)
            {
                if (SetCursorPosition(0, row))
                    WriteText(new string('X', Console.WindowWidth));
            }
        }

        private void OutputNotifications()
        {
            const int NotificationCount = 5;

            var recentNotifications = notifications.ToList();
            recentNotifications.Reverse();
            recentNotifications = recentNotifications.Take(NotificationCount).ToList();
            recentNotifications.Reverse();
            for (int i = 0; i < recentNotifications.Count; i++)
            {
                var row = GetSpecialRow() - (recentNotifications.Count - i);
                if (SetCursorPosition(0, row))
                    WriteText(recentNotifications[i].Text.FitLeft(Console.WindowWidth, Ellipsis));
            }
        }

        private bool SetCursorPosition(int left, int top)
        {
            if ((left < 0) || (left >= Console.WindowWidth) || (top < 0) || (top >= Console.WindowHeight)) return false;

            Console.SetCursorPosition(left, top);

            return true;
        }

        private string OutputIncome()
        {
            var incomeSummary = app.GetIncomeSummaryText();
            if (SetCursorPosition(Console.WindowWidth - (isWindows ? 1 : 0) - incomeSummary.Length, Console.WindowHeight - 1))
            {
                WriteText(incomeSummary, ConsoleColor.White, ConsoleColor.DarkGray);
                return incomeSummary;
            }
            return String.Empty;
        }

        private void WriteText(string text, ConsoleColor foreground = ConsoleColor.Gray, ConsoleColor background = ConsoleColor.Black)
        {
            ConsoleColor oldForeground = Console.ForegroundColor;
            ConsoleColor oldBackground = Console.BackgroundColor;
            Console.ForegroundColor = foreground;
            Console.BackgroundColor = background;
            Console.Write(text);
            Console.ForegroundColor = oldForeground;
            Console.BackgroundColor = oldBackground;
            SetCursorPosition(0, 0);
        }

        private void FillLastCell()
        {
            var row = Console.WindowHeight - 1;
            if (SetCursorPosition(0, row))
            {
                //http://stackoverflow.com/questions/25084384/filling-last-line-in-console
                WriteText(" ", ConsoleColor.Gray, ConsoleColor.DarkGray);
                Console.MoveBufferArea(0, row, 1, 1, Console.WindowWidth - 1, row);
            }
        }

        private void OutputInput(int totalWidth)
        {
            const string Prefix = "> ";
            var row = Console.WindowHeight - 1;
            if (SetCursorPosition(0, row))
            {
                SetCursorPosition(0, row);
                var width = totalWidth - Prefix.Length - (isWindows ? 1 : 0);
                var text = String.Format("{0}{1}", Prefix, CurrentInput.TrimStart().FitRight(width, Ellipsis));
                WriteText(text, ConsoleColor.White, ConsoleColor.DarkGray);
            }
        }

        private void OutputStatus()
        {
            const int Part1Width = 16;
            var deviceStatus = String.Format("{0} device(s)", app.GetVisibleDeviceCount()).FitRight(Part1Width, Ellipsis);
            var hashrateStatus = app.GetHashRateStatusText().Replace("   ", " ").FitLeft(Console.WindowWidth - deviceStatus.Length, Ellipsis);
            if (SetCursorPosition(0, Console.WindowHeight - 2))
            {
                var text = String.Format("{0}{1}", deviceStatus, hashrateStatus);
                WriteText(text, ConsoleColor.White, ConsoleColor.DarkGray);
            }
        }
        
        private void OutputSpecial()
        {
            var output = String.Empty;
            if (currentPrompt != null)
            {
                if ((DateTime.Now - promptTime).TotalSeconds > 30)
                    currentPrompt = null;
                else
                {
                    var text = String.Format("{0}: {1}", currentPrompt.Caption, currentPrompt.Text);
                    output = text.FitRight(Console.WindowWidth, Ellipsis);
                    if (SetCursorPosition(0, GetSpecialRow()))
                        WriteText(output, ConsoleColor.White, 
                            currentPrompt.Icon == PromptIcon.Error 
                            ? ConsoleColor.DarkRed 
                            : currentPrompt.Icon == PromptIcon.Warning
                            ? ConsoleColor.DarkYellow : ConsoleColor.DarkBlue);
                    return; //early exit, prompt rendered
                }
            }

            output = currentProgress.FitRight(Console.WindowWidth, Ellipsis);
            if (SetCursorPosition(0, GetSpecialRow()))
                WriteText(output, ConsoleColor.White, String.IsNullOrEmpty(currentProgress) ? ConsoleColor.Black : ConsoleColor.DarkBlue);
        }

        private static int GetSpecialRow()
        {
            return Console.WindowHeight - 3;
        }

        private void AddNotification(string text)
        {
            notifications.Add(new NotificationEventArgs
            {
                Text = text
            });
            RenderScreen();
        }

        private void OutputDevices()
        {
            var minerForm = app.GetViewModelToView();
            var devices = minerForm.Devices
                .Where(d => d.Visible)
                .ToList();

            for (int i = 0; i < devices.Count; i++)
            {
                var device = devices[i];
                var name = String.IsNullOrEmpty(device.FriendlyName) ? device.Name : device.FriendlyName;
                var hashrate = device.CurrentHashrate.ToHashrateString().Replace(" ", "");
                var coinSymbol = device.Coin == null ? String.Empty : device.Coin.Id.ShortCoinSymbol();
                var exchange = app.GetExchangeRate(device);
                var pool = device.Pool.DomainFromHost();
                var kind = device.Kind.ToString().First();
                var difficulty = device.Difficulty.ToDifficultyString().Replace(" ", "");

                if (SetCursorPosition(0, i))
                    WriteText(kind.ToString().PadRight(2), device.Enabled ? ConsoleColor.Gray : ConsoleColor.DarkGray);

                if (SetCursorPosition(2, i))
                    WriteText(name.PadFitRight(12, Ellipsis), device.Enabled ? device.Kind == Xgminer.Data.DeviceKind.NET || app.MiningEngine.Mining ? ConsoleColor.White : ConsoleColor.Gray : ConsoleColor.DarkGray);

                if (SetCursorPosition(14, i))
                    WriteText(coinSymbol.PadFitRight(8, Ellipsis), device.Enabled ? ConsoleColor.Gray : ConsoleColor.DarkGray);

                if (SetCursorPosition(21, i))
                    WriteText(difficulty.PadFitLeft(8, Ellipsis), ConsoleColor.DarkGray);

                if (SetCursorPosition(29, i))
                    WriteText(exchange.FitCurrency(9).PadLeft(10).PadRight(11), device.Enabled ? ConsoleColor.Gray : ConsoleColor.DarkGray);

                if (SetCursorPosition(40, i))
                    WriteText(pool.PadFitRight(15, Ellipsis), ConsoleColor.DarkGray);

                var left = 55;
                if (SetCursorPosition(left, i))
                    WriteText(hashrate.FitLeft(10, Ellipsis).PadRight(Console.WindowWidth - left), device.Enabled ? ConsoleColor.Gray : ConsoleColor.DarkGray);
            }

            for (int i = devices.Count; i < GetSpecialRow(); i++)
                ClearRow(i);
        }

        private void ClearRow(int row)
        {
            if (SetCursorPosition(0, row))
                WriteText(new string(' ', Console.WindowWidth));
        }
        
        private bool InputMatchesCommand(string input, string command)
        {
            var firstWord = input.Split(' ').First().TrimStart('/');
            var alias = new String(command.Where(c => Char.IsUpper(c)).ToArray());
            return firstWord.Equals(command, StringComparison.OrdinalIgnoreCase)
                || (!String.IsNullOrEmpty(alias) && firstWord.Equals(alias, StringComparison.OrdinalIgnoreCase));
        }

        private void HandleSwitchAllCommand(string input)
        {
            var parts = input.Split(' ');
            if (parts.Count() == 2)
                app.SetAllDevicesToCoin(parts[1], true);
            else
                AddNotification(String.Format("{0} symbol", SwitchAllCommand.ToLower()));
        }

        private void HandleScreenCommand(string input)
        {
            var parts = input.Split(' ');
            if (parts.Count() == 2)
            {
                var screenName = parts[1];
                if (screenName.Equals(Screen.Repl.ToString(), StringComparison.OrdinalIgnoreCase))
                    currentScreen = Screen.Repl;
                else
                    currentScreen = Screen.Main;
                RenderScreen();
            }
            else
            {
                if (currentScreen == Screen.Main)
                    currentScreen = Screen.Repl;
                else
                    currentScreen = Screen.Main;
                RenderScreen();
            }
        }

        private void HandlePoolCommand(string input)
        {
            var syntax = String.Format("{0} {{ add | remove | list }} symbol url user pass", PoolCommand.ToLower());
            var parts = input.Split(' ');

            if (parts.Count() >= 2)
            {
                var verb = parts[1];

                bool add = verb.Equals(AddVerb, StringComparison.OrdinalIgnoreCase);
                bool remove = verb.Equals(RemoveVerb, StringComparison.OrdinalIgnoreCase);
                bool list = verb.Equals(ListVerb, StringComparison.OrdinalIgnoreCase);

                if (list)
                {
                    var symbol = String.Empty;
                    if (parts.Count() >= 3)
                        symbol = parts[2];

                    HandlePoolListCommand(symbol);
                }
                else if(parts.Count() >= 4)
                {
                    var symbol = parts[2];
                    var url = parts[3];

                    CoinApi.Data.CoinInformation coin = app.CoinApiInformation.SingleOrDefault(c => c.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
                    if (coin == null)
                    {
                        AddNotification(String.Format("Unknown coin: {0}", symbol));
                        return; //early exit
                    }


                    if (add && (parts.Count() == 6))
                    {
                        var user = parts[4];
                        var pass = parts[5];

                        app.AddNewPool(coin, url, user, pass);
                    }
                    else if (remove)
                    {
                        var user = parts.Count() > 4 ? parts[4] : String.Empty;

                        app.RemoveExistingPool(coin, url, user);
                    }
                    else
                        AddNotification(syntax);
                }
                else
                    AddNotification(syntax);
            }
            else
                AddNotification(syntax);
        }

        private void HandlePoolListCommand(string symbol)
        {
            var configs = app.EngineConfiguration.CoinConfigurations
                .Where(c => String.IsNullOrEmpty(symbol)
                    || (c.PoolGroup.Id.Equals(symbol, StringComparison.OrdinalIgnoreCase)
                    || (c.PoolGroup.Id.ShortCoinSymbol().Equals(symbol, StringComparison.OrdinalIgnoreCase))));

            foreach (var config in configs)
            {
                config.Pools.ForEach((p) =>
                {
                    replBuffer.Add(config.PoolGroup.Id.ShortCoinSymbol().PadFitRight(8, Ellipsis) + ": " + p.Host.ShortHostFromHost());
                });
            }

            currentScreen = Screen.Repl;
            RenderScreen();
        }
    }
}
