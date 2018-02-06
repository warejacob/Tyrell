﻿using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Nest;
using Newtonsoft.Json;
using Tyrell.Data;
using Tyrell.DisplayConsole;

namespace Tyrell.Business
{
    public static class Functions
    {
        private static readonly ElasticClient _elasticSearch;
        private static readonly ElasticClient _elasticSearchReminders;
        private static DateTime _reminderLastRan;

        static Functions()
        {
            _reminderLastRan = DateTime.Now;

            var settings = new ConnectionSettings(new Uri(Constants.ElasticUrl))
                .DefaultIndex(Constants.ElasticPostIndex);

            _elasticSearch = new ElasticClient(settings);

            settings = new ConnectionSettings(new Uri(Constants.ElasticUrl))
                .DefaultIndex(Constants.ElasticRemindersIndex);

            _elasticSearchReminders = new ElasticClient(settings);
        }

        //the real meat of the bot
        public static async Task AutomaticMode()
        {
            while (true)
            {
                //used to offset the time that we index for when we check for reminders
                var exactStart = DateTime.Now;

                Display.FlickerPrint("[AUTOMATIC MODE] STARTING");

                await Crawler.ReadLatestForumPostsSmart();

                Display.FlickerPrint("[AUTOMATIC MODE] CHECKING FOR NEW REMINDERS");
                await CheckForRemindMePosts();

                Display.FlickerPrint("[AUTOMATIC MODE] PROCESSING REMINDME MESSAGES");
                await ProcessReminders();

                Display.FlickerPrint("[AUTOMATIC MODE] DOING ALL OTHER FUNCTIONS");
                await CheckForAllFunctions(exactStart);

                Display.FlickerPrint("[AUTOMATIC MODE] ALL OK");
                Console.Clear();
                Display.WriteOnBottomLine($"[AUTOMATIC MODE] [{DateTime.Now.ToString("G")}] SLEEPING | LAST REMINDERS : {_reminderLastRan.ToString("t")}");
                Thread.Sleep(60000);
            }
        }

        //check the posts index for remindme messages and add them to the remindme index for processing later
        //decided to keep this seperate from other functions for new user simplicity. Everyone knows remindme, not everyone wants to use advanced tyrell verb commands
        public static async Task CheckForRemindMePosts()
        {
            var esResponse = _elasticSearch.Search<ForumPost>(s => s
                .Query(q => q
                    .Bool(b => b
                        .Must(m => m
                                       .Match(mm => mm
                                           .Field(field => field.PostRaw)
                                           .Query("remindme")
                                       )
                                   && m
                                       .DateRange(r => r
                                           .Field(fieldRange => fieldRange.CreatedAt)
                                           .GreaterThanOrEquals(DateTime.Now.AddDays(-1))
                                       )
                        )
                    )
                ));

            var results = esResponse.Documents?.ToList();
            if (results != null && results.Any())
            {
                foreach (var reminderPost in results)
                {
                    try
                    {
                        var fullMessage = reminderPost.PostRaw.Trim().ToLower();

                        //ignore flag: tyrellignore
                        if (!string.IsNullOrWhiteSpace(fullMessage) && fullMessage.Contains(Constants.BotIgnoreFlag)){
                            continue;
                        }

                        //two hours cause we in SA bebe
                        var actualDate = reminderPost.CreatedAt.ToLocalTime();

                        var reminderObj = new Reminder
                        {
                            Id = reminderPost.TopicId + "-" + reminderPost.AuthorID + "-" + actualDate,
                            TopicId = reminderPost.TopicId,
                            AuthorID = reminderPost.AuthorID,
                            AuthorUserName = reminderPost.AuthorUserName,
                            ReminderRequestedOn = actualDate,
                            RemindUserOn = GetDateToRemind(fullMessage, actualDate),
                            AddedToTyrell = DateTime.Now
                        };

                        var _index = _elasticSearchReminders.Index(reminderObj);

                        await LikePost(reminderPost.Id);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                    }
                }
            }
        }

        //get and process all reminders for this run
        public static async Task ProcessReminders()
        {
            var now = DateTime.Now;

            var esResponse = _elasticSearchReminders.Search<Reminder>(s => s
                .Query(q => q
                    .Bool(b => b
                        .Must(m => m
                            .DateRange(r => r
                                .Field(fieldRange => fieldRange.RemindUserOn)
                                .GreaterThanOrEquals(_reminderLastRan)
                                .LessThanOrEquals(now)
                            )
                        )
                    )
                ));

            var results = esResponse.Documents?.ToList();
            if (results != null && results.Any())
            {
                foreach (var reminder in results)
                {
                    await PostRemindMeMessage(reminder);
                }
            }

            _reminderLastRan = now;
        }

        //comment on threads for reminders
        public static async Task PostRemindMeMessage(Reminder reminder)
        {
            await PostToThread(reminder.TopicId, $"Hey @{reminder.AuthorUserName} you asked me to remind you of this on {reminder.ReminderRequestedOn.ToString("f")}.");
        }

        //check and do all the other functions
        public static async Task CheckForAllFunctions(DateTime exactStart)
        {
            var esResponse = _elasticSearch.Search<ForumPost>(s => s
                .Query(q => q
                    .Bool(b => b
                        .Must(m => m
                                       .Match(mm => mm
                                           .Field(field => field.PostRaw)
                                           .Query("tyrell")
                                       )
                                   && m
                                       .DateRange(r => r
                                           .Field(fieldRange => fieldRange.CreatedAt)
                                           .GreaterThanOrEquals(exactStart.AddMinutes(-1))
                                       )
                        )
                    )
                ));

            var results = esResponse.Documents?.ToList();
            if (results != null && results.Any())
            {
                foreach (var possibleFunctionPost in results)
                {
                    try
                    {
                        var fullMessage = possibleFunctionPost.PostRaw.Trim().ToLower();

                        //ignore flag: tyrellignore
                        if (!string.IsNullOrWhiteSpace(fullMessage) && fullMessage.Contains(Constants.BotIgnoreFlag))
                        {
                            continue;
                        }

                        //do a thing
                        var cleanStart = fullMessage.Substring(fullMessage.IndexOf(Constants.BotName, StringComparison.Ordinal) + 7);
                        if (cleanStart.Length > 51)
                        {
                            //cut the rest of the whole post, we only need the first bit
                            cleanStart = cleanStart.Substring(0, 50);
                        }
                            
                        var commands = cleanStart.ToLower().Split(" ");
                        var target = "Himself";

                        if (commands != null && commands.Any())
                        {
                            foreach (var command in commands)
                            {
                                switch (command)
                                {
                                    case "slap":
                                        if (!string.IsNullOrWhiteSpace(commands[1]))
                                        {
                                            target = commands[1].Contains("@") ? commands[1] : "@" + commands[1];
                                        }

                                        await PostToThread(possibleFunctionPost.TopicId, $"_{possibleFunctionPost.AuthorUserName} slaps {target} around a bit with a large trout._");
                                        await LikePost(possibleFunctionPost.Id);
                                        break;

                                    case "poke":
                                        if (!string.IsNullOrWhiteSpace(commands[1]))
                                        {
                                            target = commands[1].Contains("@") ? commands[1] : "@" + commands[1];
                                        }

                                        await PostToThread(possibleFunctionPost.TopicId, $"_{possibleFunctionPost.AuthorUserName} pokes {target}._");
                                        await LikePost(possibleFunctionPost.Id);
                                        break;
                                }
                            }
                        }

                        Thread.Sleep(100);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                    }
                }
            }
        }

        //comment manually goes here, or from our functions
        public static async Task PostToThread(int threadId, string message)
        {
            try
            {
                using (HttpClient client = new HttpClient())
                {
                    var uri = new Uri($"{Constants.ForumBaseUrl}/posts");

                    client.DefaultRequestHeaders.Accept.Clear();
                    client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                    client.DefaultRequestHeaders.Add("x-csrf-token", await Session.GetCsrfToken());
                    client.DefaultRequestHeaders.Add("cookie", Session.Cookie);
                    client.DefaultRequestHeaders.Add("x-requested-with", "XMLHttpRequest");

                    var post = new
                    {
                        archetype = "regular",
                        raw = message,
                        topic_id = threadId
                    };

                    var request = new HttpRequestMessage(HttpMethod.Post, uri);
                    request.Headers.Add("x-csrf-token", await Session.GetCsrfToken());
                    request.Headers.Add("x-requested-with", "XMLHttpRequest");
                    request.Headers.Add("cookie", Session.Cookie);
                    request.Method = HttpMethod.Post;
                    request.Content = new StringContent(JsonConvert.SerializeObject(post));
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                    HttpResponseMessage response = await client.SendAsync(request);
                    if (response.IsSuccessStatusCode)
                    {
                        //jay
                        Display.FlickerPrint("MESSAGE POSTED");
                    }
                }
            }
            catch (Exception w)
            {
                Console.WriteLine(w);
                Thread.Sleep(2000);
            }
        }

        //Like a post, as in to mark it as registered with remindme or the other functions
        public static async Task LikePost(int postId)
        {
            try
            {
                using (HttpClient client = new HttpClient())
                {
                    var uri = new Uri($"{Constants.ForumBaseUrl}/post_actions");

                    client.DefaultRequestHeaders.Accept.Clear();
                    client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                    client.DefaultRequestHeaders.Add("x-csrf-token", await Session.GetCsrfToken());
                    client.DefaultRequestHeaders.Add("cookie", Session.Cookie);
                    client.DefaultRequestHeaders.Add("x-requested-with", "XMLHttpRequest");

                    var post = new 
                    {
                        id = postId,
                        post_action_type_id = 2
                    };

                    var request = new HttpRequestMessage(HttpMethod.Post, uri);
                    request.Headers.Add("x-csrf-token", await Session.GetCsrfToken());
                    request.Headers.Add("x-requested-with", "XMLHttpRequest");
                    request.Headers.Add("cookie", Session.Cookie);
                    request.Method = HttpMethod.Post;
                    request.Content = new StringContent(JsonConvert.SerializeObject(post));
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                    HttpResponseMessage response = await client.SendAsync(request);
                    if (response.IsSuccessStatusCode)
                    {
                        //jay
                        Display.WriteOnBottomLine($"[{postId}] POST LIKED");
                    }
                }
            }
            catch (Exception w)
            {
                Console.WriteLine(w);
                Thread.Sleep(2000);
            }
        }

        //this could do with some improvement
        private static DateTime GetDateToRemind(string fullMessage, DateTime dateForReminder)
        {
            var cleanStart = fullMessage.Substring(fullMessage.IndexOf("remindme", StringComparison.Ordinal) + 9);
            if (cleanStart.Length > 31)
            {
                //cut the rest of the whole post, we only need the first bit
                cleanStart = cleanStart.Substring(0, 30);
            }

            var commands = cleanStart.ToLower().Split(" ");

            if (commands != null && commands.Any())
            {
                var carryInfo = 0;
                foreach (var command in commands)
                {
                    //skip the 'in' and 'at' words, or somehow the bot name
                    if (command == "in" || command == "at" || command == Constants.BotName)
                    {
                        continue;
                    }
                    
                    //try check for definitive reminders
                    if (command == "tomorrow")
                    {
                        dateForReminder = DateTime.Today.AddDays(1).AddHours(7).AddMinutes(30);
                        break;
                    }

                    if (command == "tonight")
                    {
                        dateForReminder = DateTime.Today.AddHours(18);
                        break;
                    }

                    //check against wordy folks
                    if (command == "one")
                    {
                        carryInfo = 1;
                        continue;
                    }

                    if (command == "two")
                    {
                        carryInfo = 2;
                        continue;
                    }

                    if (command == "three")
                    {
                        carryInfo = 3;
                        continue;
                    }

                    if (command == "four")
                    {
                        carryInfo = 4;
                        continue;
                    }

                    if (command == "five")
                    {
                        carryInfo = 5;
                        continue;
                    }

                    if (command == "six")
                    {
                        carryInfo = 6;
                        continue;
                    }

                    if (command == "seven")
                    {
                        carryInfo = 7;
                        continue;
                    }

                    if (command == "eight")
                    {
                        carryInfo = 8;
                        continue;
                    }

                    if (command == "nine")
                    {
                        carryInfo = 9;
                        continue;
                    }

                    if (command == "ten")
                    {
                        carryInfo = 10;
                        continue;
                    }
                   
                    //check for range
                    if (command == "hour" || command == "hours")
                    {
                        dateForReminder = dateForReminder.AddHours(carryInfo);
                        continue;
                    }

                    if (command == "day" || command == "days")
                    {
                        dateForReminder = dateForReminder.AddDays(carryInfo);
                        continue;
                    }

                    if (command == "week" || command == "weeks")
                    {
                        dateForReminder = dateForReminder.AddDays(carryInfo * 7);
                        continue;
                    }

                    if (command == "month" || command == "months")
                    {
                        dateForReminder = dateForReminder.AddMonths(carryInfo);
                        continue;
                    }
                    
                    //check manual format
                    if (command.Contains("/"))
                    {
                        try
                        {
                            dateForReminder = DateTime.ParseExact(command, "d/M/yyyy", CultureInfo.InvariantCulture).AddHours(7).AddMinutes(30).ToLocalTime();
                            break;
                        }
                        catch { }

                        try
                        {
                            dateForReminder = DateTime.ParseExact(command, "dd/MM/yyyy", CultureInfo.InvariantCulture).AddHours(7).AddMinutes(30).ToLocalTime();
                            break;
                        }
                        catch { }

                        try
                        {
                            dateForReminder = DateTime.ParseExact(command, "d/MM/yyyy", CultureInfo.InvariantCulture).AddHours(7).AddMinutes(30).ToLocalTime();
                            break;
                        }
                        catch { }

                        try
                        {
                            dateForReminder = DateTime.ParseExact(command, "dd/M/yyyy", CultureInfo.InvariantCulture).AddHours(7).AddMinutes(30).ToLocalTime();
                            break;
                        }
                        catch { }
                    }

                    //check explicit reminder times
                    //e.g 7:30pm | 7:30am
                    if (command.Contains(":"))
                    {
                        if (command.Contains("pm"))
                        {
                            try
                            {
                                var fullTime = command.Replace("pm", "");
                                var hour = fullTime.Substring(0, fullTime.IndexOf(":"));
                                var minutes = fullTime.Substring(fullTime.IndexOf(":") + 1);

                                dateForReminder = (dateForReminder.Date).AddHours(12).AddHours(Convert.ToInt32(hour)).AddMinutes(Convert.ToInt32(minutes));
                                break;
                            }
                            catch { }
                        }

                        if (command.Contains("am"))
                        {
                            try
                            {
                                var fullTime = command.Replace("am", "");
                                var hour = fullTime.Substring(0, fullTime.IndexOf(":"));
                                var minutes = fullTime.Substring(fullTime.IndexOf(":") + 1);

                                dateForReminder = (dateForReminder.Date).AddHours(Convert.ToInt32(hour)).AddMinutes(Convert.ToInt32(minutes));
                                break;
                            }
                            catch { }
                        }

                        //malformed command with ':'
                        break;
                    }

                    //eg 7pm
                    if (command.Contains("pm"))
                    {
                        try
                        {
                            dateForReminder = (dateForReminder.Date).AddHours(12).AddHours(Convert.ToInt32(command.Replace("pm", "")));
                        }
                        catch { }
                        break;
                    }

                    //eg 6am
                    if (command.Contains("am"))
                    {
                        try
                        {
                            dateForReminder = (dateForReminder.Date).AddHours(Convert.ToInt32(command.Replace("am", "")));
                        }
                        catch { }
                        break;
                    }

                    //could not get carry from word or it wasnt one of the others, try parse it
                    if (carryInfo == 0)
                    {
                        int.TryParse(command, out carryInfo);
                        continue;
                    }

                    //if we end up here, who knows how we got here. Kill it
                    dateForReminder = DateTime.MinValue;
                    break;
                }
            }
            
            return dateForReminder;
        }
    }
}