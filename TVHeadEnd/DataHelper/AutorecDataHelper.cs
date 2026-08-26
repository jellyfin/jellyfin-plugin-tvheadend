using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging;
using TVHeadEnd.HTSP;

namespace TVHeadEnd.DataHelper
{
    public class AutorecDataHelper
    {
        private readonly ILogger<AutorecDataHelper> _logger;
        private readonly Dictionary<string, HTSMessage> _data;

        public AutorecDataHelper(ILogger<AutorecDataHelper> logger)
        {
            _logger = logger;
            _data = new Dictionary<string, HTSMessage>();
        }

        public void clean()
        {
            lock (_data)
            {
                _data.Clear();
            }
        }

        public void autorecEntryAdd(HTSMessage message)
        {
            string id = message.getString("id");
            lock (_data)
            {
                if (_data.ContainsKey(id))
                {
                    _logger.LogDebug("[TVHclient] AutorecDataHelper.autorecEntryAdd: id already in database - skipping");
                    return;
                }
                _data.Add(id, message);
            }
        }

        public void autorecEntryUpdate(HTSMessage message)
        {
            string id = message.getString("id");
            lock (_data)
            {
                HTSMessage oldMessage = _data[id];
                if (oldMessage == null)
                {
                    _logger.LogDebug("[TVHclient] AutorecDataHelper.autorecEntryAdd: id not in database - skipping");
                    return;
                }
                foreach (KeyValuePair<string, object> entry in message)
                {
                    if (oldMessage.containsField(entry.Key))
                    {
                        oldMessage.removeField(entry.Key);
                    }
                    oldMessage.putField(entry.Key, entry.Value);
                }
            }
        }

        public void autorecEntryDelete(HTSMessage message)
        {
            string id = message.getString("id");
            lock (_data)
            {
                _data.Remove(id);
            }
        }

        public Task<IEnumerable<SeriesTimerInfo>> buildAutorecInfos(CancellationToken cancellationToken)
        {
            return Task.Factory.StartNew<IEnumerable<SeriesTimerInfo>>(() =>
            {
                lock (_data)
                {
                    List<SeriesTimerInfo> result = new List<SeriesTimerInfo>();

                    foreach (KeyValuePair<string, HTSMessage> entry in _data)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            _logger.LogDebug("[TVHclient] AutorecDataHelper.buildAutorecInfos: call cancelled - returning partial list");
                            return result;
                        }

                        HTSMessage m = entry.Value;
                        SeriesTimerInfo sti = new SeriesTimerInfo
                        {
                            RecordAnyChannel = true,
                            RecordAnyTime = true
                        };

                        try
                        {
                            if (m.containsField("id"))
                            {
                                sti.Id = m.getString("id");
                            }
                        }
                        catch (InvalidCastException ex)
                        {
                            logInvalidField(ex, "id", entry.Key);
                        }

                        try
                        {
                            if (m.containsField("daysOfWeek"))
                            {
                                int daysOfWeek = m.getInt("daysOfWeek");
                                sti.Days = getDayOfWeekListFromInt(daysOfWeek);
                            }
                        }
                        catch (InvalidCastException ex)
                        {
                            logInvalidField(ex, "daysOfWeek", entry.Key);
                        }

                        sti.StartDate = DateTime.UtcNow;
                        sti.EndDate = sti.StartDate;

                        // TVHeadend's retention field is the lifetime of completed
                        // recordings. SeriesTimerInfo has no equivalent property;
                        // EndDate instead represents the end of the daily match window.

                        try
                        {
                            if (m.containsField("start"))
                            {
                                int startMinutes = m.getInt("start");
                                if (startMinutes >= 0)
                                {
                                    int endMinutes = m.getInt("startWindow", startMinutes);
                                    sti.StartDate = DateTime.Today.AddMinutes(startMinutes).ToUniversalTime();
                                    sti.EndDate = DateTime.Today.AddMinutes(endMinutes).ToUniversalTime();
                                    // A window whose end is earlier than its start wraps across midnight.
                                    if (sti.EndDate < sti.StartDate)
                                    {
                                        sti.EndDate = sti.EndDate.AddDays(1);
                                    }

                                    sti.RecordAnyTime = false;
                                }
                                else
                                {
                                    sti.RecordAnyTime = true;
                                }
                            }
                            else
                            {
                                sti.RecordAnyTime = true;
                            }
                        }
                        catch (InvalidCastException ex)
                        {
                            sti.RecordAnyTime = true;
                            logInvalidField(ex, "start/startWindow", entry.Key);
                        }

                        try
                        {
                            if (m.containsField("channel"))
                            {
                                sti.ChannelId = "" + m.getInt("channel");
                                sti.RecordAnyChannel = false;
                            }
                            else
                            {
                                sti.RecordAnyChannel = true;
                            }
                        }
                        catch (InvalidCastException ex)
                        {
                            sti.RecordAnyChannel = true;
                            logInvalidField(ex, "channel", entry.Key);
                        }

                        try
                        {
                            if (m.containsField("startExtra"))
                            {
                                sti.PrePaddingSeconds = (int)m.getLong("startExtra") * 60;
                                sti.IsPrePaddingRequired = true;
                            }
                        }
                        catch (InvalidCastException ex)
                        {
                            logInvalidField(ex, "startExtra", entry.Key);
                        }

                        try
                        {
                            if (m.containsField("stopExtra"))
                            {
                                sti.PostPaddingSeconds = (int)m.getLong("stopExtra") * 60;
                                sti.IsPostPaddingRequired = true;
                            }
                        }
                        catch (InvalidCastException ex)
                        {
                            logInvalidField(ex, "stopExtra", entry.Key);
                        }

                        try
                        {
                            if (m.containsField("name"))
                            {
                                sti.Name = m.getString("name");
                            }
                            else if (m.containsField("title"))
                            {
                                sti.Name = m.getString("title");
                            }
                        }
                        catch (InvalidCastException ex)
                        {
                            logInvalidField(ex, "name/title", entry.Key);
                        }

                        try
                        {
                            if (m.containsField("description"))
                            {
                                sti.Overview = m.getString("description");
                            }
                        }
                        catch (InvalidCastException ex)
                        {
                            logInvalidField(ex, "description", entry.Key);
                        }

                        try
                        {
                            if (m.containsField("priority"))
                            {
                                sti.Priority = m.getInt("priority");
                            }
                        }
                        catch (InvalidCastException ex)
                        {
                            logInvalidField(ex, "priority", entry.Key);
                        }

                        try
                        {
                            if (m.containsField("serieslinkUri"))
                            {
                                sti.SeriesId = m.getString("serieslinkUri");
                            }
                        }
                        catch (InvalidCastException ex)
                        {
                            logInvalidField(ex, "serieslinkUri", entry.Key);
                        }

                        try
                        {
                            if (m.containsField("dupDetect"))
                            {
                                sti.RecordNewOnly = m.getInt("dupDetect") != 0;
                            }
                        }
                        catch (InvalidCastException ex)
                        {
                            logInvalidField(ex, "dupDetect", entry.Key);
                        }

                        try
                        {
                            if (m.containsField("maxCount"))
                            {
                                sti.KeepUpTo = m.getInt("maxCount");
                            }
                        }
                        catch (InvalidCastException ex)
                        {
                            logInvalidField(ex, "maxCount", entry.Key);
                        }

                        result.Add(sti);
                    }

                    return result;
                }
            });
        }

        private void logInvalidField(InvalidCastException exception, string fieldName, string autorecId)
        {
            _logger.LogWarning(
                exception,
                "[TVHclient] AutorecDataHelper.buildAutorecInfos: could not read field '{field}' for autorecord entry '{id}'",
                fieldName,
                autorecId);
        }

        private List<DayOfWeek> getDayOfWeekListFromInt(int daysOfWeek)
        {
            List<DayOfWeek> result = new List<DayOfWeek>();
            if ((daysOfWeek & 0x01) != 0)
            {
                result.Add(DayOfWeek.Monday);
            }
            if ((daysOfWeek & 0x02) != 0)
            {
                result.Add(DayOfWeek.Tuesday);
            }
            if ((daysOfWeek & 0x04) != 0)
            {
                result.Add(DayOfWeek.Wednesday);
            }
            if ((daysOfWeek & 0x08) != 0)
            {
                result.Add(DayOfWeek.Thursday);
            }
            if ((daysOfWeek & 0x10) != 0)
            {
                result.Add(DayOfWeek.Friday);
            }
            if ((daysOfWeek & 0x20) != 0)
            {
                result.Add(DayOfWeek.Saturday);
            }
            if ((daysOfWeek & 0x40) != 0)
            {
                result.Add(DayOfWeek.Sunday);
            }
            return result;
        }

        public static int getDaysOfWeekFromList(List<DayOfWeek> days)
        {
            int result = 0;
            foreach (DayOfWeek currDay in days)
            {
                switch (currDay)
                {
                    case DayOfWeek.Monday:
                        result = result | 0x1;
                        break;
                    case DayOfWeek.Tuesday:
                        result = result | 0x2;
                        break;
                    case DayOfWeek.Wednesday:
                        result = result | 0x4;
                        break;
                    case DayOfWeek.Thursday:
                        result = result | 0x8;
                        break;
                    case DayOfWeek.Friday:
                        result = result | 0x10;
                        break;
                    case DayOfWeek.Saturday:
                        result = result | 0x20;
                        break;
                    case DayOfWeek.Sunday:
                        result = result | 0x40;
                        break;
                }
            }
            return result;
        }

        public static int getMinutesFromMidnight(DateTime time)
        {
            DateTime localTime = time.ToLocalTime();
            int hours = localTime.Hour;
            int minute = localTime.Minute;
            int minutes = (hours * 60) + minute;
            return minutes;
        }
    }
}
