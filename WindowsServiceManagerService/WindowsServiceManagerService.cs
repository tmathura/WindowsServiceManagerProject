using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Threading.Tasks;
using System.Xml;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using System.Timers;

namespace WindowsServiceManagerService
{
    public partial class WindowsServiceManagerService : ServiceBase
    {
        private TelegramBotClient _telegramBot;
        private Timer _timer;

        public WindowsServiceManagerService()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            var accessToken = ConfigurationManager.AppSettings["TelegramAccessToken"];

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                using (var eventLog = new EventLog("Application"))
                {
                    eventLog.Source = "WindowsServiceManagerService";
                    eventLog.WriteEntry("Telegram: Failed to instantiate Telegram, Telegram Access Token is blank.",
                        EventLogEntryType.Error);
                }
            }
            else
            {
                _telegramBot = new TelegramBotClient(accessToken);
                var meBot = _telegramBot.GetMeAsync().Result;
                _telegramBot.OnCallbackQuery += BotOnCallbackQueryReceived;
                _telegramBot.OnMessage += BotOnMessageReceived;
                _telegramBot.OnMessageEdited += BotOnMessageReceived;
                _telegramBot.StartReceiving();

                var autoStartServicesTimer = Convert.ToInt32(ConfigurationManager.AppSettings["AutoStartServicesTimer"]);
                if (autoStartServicesTimer > 0)
                {
                    _timer = new Timer
                    {
                        Interval = autoStartServicesTimer,
                        AutoReset = true
                    };
                    _timer.Elapsed += timer_Elapsed;
                    _timer.Start();
                }

                SendTelegramMsg(accessToken, "Windows Service Manager Service started.");
            }
        }

        protected override void OnStop()
        {
            _telegramBot?.StopReceiving();
        }

        private void timer_Elapsed(object sender, ElapsedEventArgs e)
        {
            var accessToken = ConfigurationManager.AppSettings["TelegramAccessToken"];
            var serviceNames = ConfigurationManager.AppSettings["PreLoadedServices"].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();

            foreach (var serviceName in serviceNames)
            {
                try
                {
                    var service = new ServiceController(serviceName);
                    if (service.Status != ServiceControllerStatus.Running && service.Status != ServiceControllerStatus.StartPending)
                    {
                        service.Start();
                        SendTelegramMsg(accessToken, $"Auto Start Services Process: {serviceName} started.");
                    }
                }
                catch (Exception exception)
                {
                    using (var eventLog = new EventLog("Application"))
                    {
                        eventLog.Source = "WindowsServiceManagerService";
                        eventLog.WriteEntry($"Auto Start Services Process: Error starting service {serviceName}, error '{exception.Message}' .",
                            EventLogEntryType.Error);
                    }
                    SendTelegramMsg(accessToken, $"Auto Start Services Process: Error starting service {serviceName}.");
                }
            }
        }

        public async Task SendTelegramMsg(string accessToken, string msg)
        {
            try
            {
                var botClient = new TelegramBotClient(accessToken);
                var chatId = Convert.ToInt32(ConfigurationManager.AppSettings["TelegramUserChatId"]);
                if (chatId == 0)
                    using (var eventLog = new EventLog("Application"))
                    {
                        eventLog.Source = "WindowsServiceManagerService";
                        eventLog.WriteEntry(
                            "Telegram: Failed to send message to Telegram, Chat Id is 0, did you send a /start message after starting the service?",
                            EventLogEntryType.Warning);
                    }
                else
                    await botClient.SendTextMessageAsync(chatId,
                        msg.Substring(0, Math.Min(msg.Replace("_", "").Length, 4096)));
            }
            catch (Exception ex)
            {
                using (var eventLog = new EventLog("Application"))
                {
                    eventLog.Source = "WindowsServiceManagerService";
                    eventLog.WriteEntry($"Telegram: Failed to send message to Telegram - {ex.Message}",
                        EventLogEntryType.Warning);
                }
            }
        }

        public async void BotOnCallbackQueryReceived(object sender, CallbackQueryEventArgs callbackQueryEventArgs)
        {
            try
            {
                var callbackQuery = callbackQueryEventArgs.CallbackQuery;
                try
                {
                    var callBackCommand = callbackQuery.Data.Split('#')[0];
                    var callBackData = callbackQuery.Data.Split('#')[1];
                    var service = new ServiceController(callBackData);

                    switch (callBackCommand)
                    {
                        case "Close":
                            if (service.Status == ServiceControllerStatus.Stopped)
                                throw new ArgumentException($"{service.DisplayName} is stopped, unable to close it.");
                            service.Close();
                            await _telegramBot.AnswerCallbackQueryAsync(
                                callbackQuery.Id,
                                $"{service.DisplayName} closed.");
                            using (var eventLog = new EventLog("Application"))
                            {
                                eventLog.Source = "WindowsServiceManagerService";
                                eventLog.WriteEntry($"{service.DisplayName} closed.", EventLogEntryType.Information);
                            }

                            break;
                        case "Continue":
                            if (service.Status == ServiceControllerStatus.ContinuePending)
                                throw new ArgumentException(
                                    $"{service.DisplayName} has a continue pending, unable to continue it.");

                            service.Continue();
                            await _telegramBot.AnswerCallbackQueryAsync(
                                callbackQuery.Id,
                                $"{service.DisplayName} continued.");
                            using (var eventLog = new EventLog("Application"))
                            {
                                eventLog.Source = "WindowsServiceManagerService";
                                eventLog.WriteEntry($"{service.DisplayName} continued.", EventLogEntryType.Information);
                            }

                            break;
                        case "Pause":
                            if (service.Status == ServiceControllerStatus.PausePending)
                                throw new ArgumentException(
                                    $"{service.DisplayName} has a pause pending, unable to pause it.");

                            service.Pause();
                            await _telegramBot.AnswerCallbackQueryAsync(
                                callbackQuery.Id,
                                $"{service.DisplayName} paused.");
                            using (var eventLog = new EventLog("Application"))
                            {
                                eventLog.Source = "WindowsServiceManagerService";
                                eventLog.WriteEntry($"{service.DisplayName} paused.", EventLogEntryType.Information);
                            }

                            break;
                        case "Restart":
                            if (service.Status == ServiceControllerStatus.Stopped)
                                throw new ArgumentException($"{service.DisplayName} is stopped, unable to restart it.");

                            var millisec1 = Environment.TickCount;
                            var timeout = TimeSpan.FromMilliseconds(7000);

                            service.Stop();
                            service.WaitForStatus(ServiceControllerStatus.Stopped, timeout);

                            var millisec2 = Environment.TickCount;
                            timeout = TimeSpan.FromMilliseconds(7000 - (millisec2 - millisec1));

                            service.Start();
                            service.WaitForStatus(ServiceControllerStatus.Running, timeout);
                            await _telegramBot.AnswerCallbackQueryAsync(
                                callbackQuery.Id,
                                $"{service.DisplayName} restarted.");
                            using (var eventLog = new EventLog("Application"))
                            {
                                eventLog.Source = "WindowsServiceManagerService";
                                eventLog.WriteEntry($"{service.DisplayName} restarted.", EventLogEntryType.Information);
                            }

                            break;
                        case "Start":
                            if (service.Status == ServiceControllerStatus.StartPending)
                                throw new ArgumentException(
                                    $"{service.DisplayName} has a start pending, unable to start it.");
                            if (service.Status == ServiceControllerStatus.Running)
                                throw new ArgumentException(
                                    $"{service.DisplayName} is already running, unable to start it.");

                            service.Start();
                            await _telegramBot.AnswerCallbackQueryAsync(
                                callbackQuery.Id,
                                $"{service.DisplayName} started.");
                            using (var eventLog = new EventLog("Application"))
                            {
                                eventLog.Source = "WindowsServiceManagerService";
                                eventLog.WriteEntry($"{service.DisplayName} started.", EventLogEntryType.Information);
                            }

                            break;
                        case "Stop":
                            if (service.Status == ServiceControllerStatus.StopPending)
                                throw new ArgumentException(
                                    $"{service.DisplayName} has a stop pending, unable to stop it.");
                            if (service.Status == ServiceControllerStatus.Stopped)
                                throw new ArgumentException(
                                    $"{service.DisplayName} is already stopped, unable to stop it.");

                            service.Stop();
                            await _telegramBot.AnswerCallbackQueryAsync(
                                callbackQuery.Id,
                                $"{service.DisplayName} stopped.");
                            using (var eventLog = new EventLog("Application"))
                            {
                                eventLog.Source = "WindowsServiceManagerService";
                                eventLog.WriteEntry($"{service.DisplayName} stopped.", EventLogEntryType.Information);
                            }

                            break;
                        default:
                            break;
                    }
                }
                catch (Exception ex)
                {
                    await _telegramBot.AnswerCallbackQueryAsync(
                        callbackQuery.Id,
                        $"Processing file error: {ex.Message}");
                    using (var eventLog = new EventLog("Application"))
                    {
                        eventLog.Source = "WindowsServiceManagerService";
                        eventLog.WriteEntry(ex.Message, EventLogEntryType.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                using (var eventLog = new EventLog("Application"))
                {
                    eventLog.Source = "WindowsServiceManagerService";
                    eventLog.WriteEntry(ex.Message, EventLogEntryType.Error);
                }
            }
        }

        public async void BotOnMessageReceived(object sender, MessageEventArgs messageEventArgs)
        {
            try
            {
                string msg;
                var message = messageEventArgs.Message;
                var text = message.Text;
                var chatId = messageEventArgs.Message.Chat.Id;

                if (message == null || message.Type != MessageType.Text) return;

                if (text == "/start")
                {
                    var xmlDoc = new XmlDocument();
                    xmlDoc.Load(AppDomain.CurrentDomain.SetupInformation.ConfigurationFile);

                    xmlDoc.SelectSingleNode("//appSettings/add[@key='TelegramUserChatId']").Attributes["value"].Value =
                        Convert.ToString(chatId);
                    xmlDoc.Save(AppDomain.CurrentDomain.SetupInformation.ConfigurationFile);

                    ConfigurationManager.RefreshSection("appSettings");

                    msg = "Welcome...Here are all my commands";
                    await _telegramBot.SendTextMessageAsync(chatId, msg, ParseMode.Default,
                        replyMarkup: CustomerKeyBoard());
                    return;
                }

                if (text == "/help")
                {
                    msg = "Here are all my commands";
                    await _telegramBot.SendTextMessageAsync(chatId, msg, ParseMode.Default,
                        replyMarkup: CustomerKeyBoard());
                    return;
                }

                if (text.Contains("/get_service"))
                {
                    var arguments = text.Split('[');
                    if (arguments.Length > 2)
                    {
                        msg = "Too many arguments passed for this command.";
                        await _telegramBot.SendTextMessageAsync(chatId, msg, ParseMode.Markdown,
                            replyMarkup: CustomerKeyBoard());
                        return;
                    }

                    if (arguments.Length < 2)
                    {
                        msg = "Too few arguments passed for this command.";
                        await _telegramBot.SendTextMessageAsync(chatId, msg, ParseMode.Markdown,
                            replyMarkup: CustomerKeyBoard());
                        return;
                    }

                    var serviceName = arguments[1].ToUpper().Replace("[", "").Replace("]", "");

                    await _telegramBot.SendChatActionAsync(chatId, ChatAction.Typing);

                    var servicesArray = ServiceController.GetServices();
                    var servicesList = (from serviceInArray in servicesArray
                        where string.Equals(serviceInArray.ServiceName, serviceName,
                            StringComparison.CurrentCultureIgnoreCase)
                        select new ServiceClass
                        {
                            Service = serviceInArray.ServiceName,
                            DisplayName = serviceInArray.DisplayName,
                            Status = serviceInArray.Status
                        }).ToList();

                    if (servicesList.Count == 0)
                        await _telegramBot.SendTextMessageAsync(chatId,
                            $"No service installed with name {serviceName}");
                    else
                        await _telegramBot.SendTextMessageAsync(
                            chatId,
                            "Choose an action:",
                            replyMarkup: CreateInLineQueuedMarkup(servicesList));
                }

                if (text.Contains("/get_status_of_below_services"))
                {
                    var serviceName = ConfigurationManager.AppSettings["PreLoadedServices"].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                    
                    await _telegramBot.SendChatActionAsync(chatId, ChatAction.Typing);

                    var servicesArray = ServiceController.GetServices();
                    var servicesList = new List<ServiceClass>();
                    foreach (var service in servicesArray)
                        if (serviceName.Contains(service.ServiceName))
                            servicesList.Add(new ServiceClass
                            {
                                Service = service.ServiceName,
                                DisplayName = service.DisplayName,
                                Status = service.Status
                            });

                    if (servicesList.Count == 0)
                    {
                        await _telegramBot.SendTextMessageAsync(chatId,
                            "No services installed with the names given.");
                    }
                    else
                    {
                        var telegramMsg = new List<string>();
                        foreach (var service in servicesList)
                            telegramMsg.Add(
                                $"Service Name: {service.DisplayName}" +
                                $"\r\nStatus: {service.Status}" +
                                string.Format("\r\n{0}{0}{0}{0}{0}{0}{0}{0}{0}{0}", "\u2796")
                            );

                        if (telegramMsg.Count == 0) telegramMsg.Add("No services installed with the names given.");

                        var result = string.Join("\r\n", telegramMsg.ToArray());
                        msg = result;
                        await _telegramBot.SendTextMessageAsync(chatId, msg, ParseMode.Markdown,
                            replyMarkup: CustomerKeyBoard());
                    }
                }
            }
            catch (Exception ex)
            {
                using (var eventLog = new EventLog("Application"))
                {
                    eventLog.Source = "WindowsServiceManagerService";
                    eventLog.WriteEntry(ex.Message, EventLogEntryType.Error);
                }
            }
        }

        public static IReplyMarkup CreateInlineKeyboardButton(Dictionary<string, string> buttonList, int columns)
        {
            var rows = (int) Math.Ceiling(buttonList.Count / (double) columns);
            var buttons = new InlineKeyboardButton[rows][];

            for (var i = 0; i < buttons.Length; i++)
                buttons[i] = buttonList
                    .Skip(i * columns)
                    .Take(columns)
                    .Select(direction => new InlineKeyboardButton { Text = direction.Value, CallbackData = direction.Key })
                    .ToArray();
            return new InlineKeyboardMarkup(buttons);
        }

        public static IReplyMarkup CreateInLineQueuedMarkup(List<ServiceClass> servicesList)
        {
            var buttonsList = new Dictionary<string, string>();
            foreach (var service in servicesList)
            {
                buttonsList.Add(
                    $"Close#{service.Service}",
                    $"Close: {service.DisplayName}");
                buttonsList.Add(
                    $"Continue#{service.Service}",
                    $"Continue: {service.DisplayName}");
                buttonsList.Add(
                    $"Pause#{service.Service}",
                    $"Pause: {service.DisplayName}");
                buttonsList.Add(
                    $"Restart#{service.Service}",
                    $"Restart: {service.DisplayName}");
                if (service.Status == ServiceControllerStatus.Running)
                    buttonsList.Add(
                        $"Start#{service.Service}",
                        $"Already started: {service.DisplayName}");
                else
                    buttonsList.Add(
                        $"Start#{service.Service}",
                        $"Start: {service.DisplayName}");
                if (service.Status == ServiceControllerStatus.Stopped)
                    buttonsList.Add(
                        $"Stop#{service.Service}",
                        $"Already stopped: {service.DisplayName}");
                else
                    buttonsList.Add(
                        $"Stop#{service.Service}",
                        $"Stop: {service.DisplayName}");
            }

            return CreateInlineKeyboardButton(buttonsList, 1);
        }

        public static ReplyKeyboardMarkup CustomerKeyBoard()
        {
            var serviceNames = ConfigurationManager.AppSettings["PreLoadedServices"].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            var buttonList = new List<string>
            {
                "/get_status_of_below_services",
                "/get_service [Insert Service Name Here]"
            };
            for (var i = 0; i < buttonList.Count; i++)
                buttonList.AddRange(serviceNames
                    .Skip(i * 1)
                    .Take(1)
                    .Select(serviceName =>($"/get_service [{serviceName}]"))
                    .ToList());
            buttonList.Add("/help");

            var buttons = new KeyboardButton[buttonList.Count][];

            for (var i = 0; i < buttons.Length; i++)
                buttons[i] = buttonList
                    .Skip(i * 1)
                    .Take(1)
                    .Select(btnText => new KeyboardButton(btnText))
                    .ToArray();

            var keyb = new ReplyKeyboardMarkup
            {
                Keyboard = buttons
            };
            return keyb;
        }
    }

    public class ServiceClass
    {
        public string Service { get; set; }
        public string DisplayName { get; set; }
        public ServiceControllerStatus Status { get; set; }
    }
}