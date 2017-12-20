#region Copyright (C) 2007-2015 Team MediaPortal

/*
    Copyright (C) 2007-2015 Team MediaPortal
    http://www.team-mediaportal.com

    This file is part of MediaPortal 2

    MediaPortal 2 is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    MediaPortal 2 is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with MediaPortal 2. If not, see <http://www.gnu.org/licenses/>.
*/

#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MediaPortal.Backend.Database;
using MediaPortal.Common;
using MediaPortal.Common.MediaManagement;
using MediaPortal.Plugins.SlimTv.Interfaces;
using MediaPortal.Plugins.SlimTv.Interfaces.Items;
using MediaPortal.Plugins.SlimTv.Interfaces.UPnP.Items;
using MediaPortal.Utilities.FileSystem;
using TvLibrary.Interfaces.Integration;
using IChannel = MediaPortal.Plugins.SlimTv.Interfaces.Items.IChannel;
using ILogger = MediaPortal.Common.Logging.ILogger;
using IPathManager = MediaPortal.Common.PathManager.IPathManager;
using ScheduleRecordingType = MediaPortal.Plugins.SlimTv.Interfaces.ScheduleRecordingType;
using MediaPortal.Utilities;
using TvLibrary.Implementations.DVB;
using TvControl;
using TvDatabase;
using TvEngine.Events;
using TvService;
using Card = TvDatabase.Card;
using IUser = TvControl.IUser;
using User = TvControl.User;
using VirtualCard = TvControl.VirtualCard;
using System.Globalization;
using Gentle.Framework;
using System.Collections;
using MediaPortal.Common.Settings;
using MediaPortal.Plugins.SlimTv.Proxy.Settings;

namespace MediaPortal.Plugins.SlimTv.Service
{
  public class SlimTvService : AbstractSlimTvService
  {
    private Timer _checkForRecordingTimer = null;
    private int _startDelay = 10000;
    private int _checkInterval = 60000;
    private Dictionary<Card, string> _currentlyRecording = new Dictionary<TvDatabase.Card, string>();
    private List<Card> _allCards = null;
    private object _recordingSync = new object();

    protected readonly Dictionary<string, IUser> _tvUsers = new Dictionary<string, IUser>();

    public SlimTvService()
    {
      _serviceName = "SlimTv.Proxy3";
    }

    #region Database and program data initialization

    protected override void PrepareIntegrationProvider()
    {
      IntegrationProviderHelper.Register(@"Plugins\" + _serviceName, "Plugins\\" + _serviceName + "\\castle.config");
      var pathManager = ServiceRegistration.Get<IPathManager>();
      pathManager.SetPath("TVCORE", "<DATA>\\SlimTVCore\\v3.0");
    }

    protected override void PrepareConnection(ITransaction transaction)
    { }

    protected override void PrepareFilterRegistrations()
    {
      Dictionary<Guid, string> filters = new Dictionary<Guid, string>
      {
        { new Guid("{D3DD4C59-D3A7-4B82-9727-7B9203EB67C0}"), "MPIPTvSource.ax"},
        { new Guid("{7F2BBEAF-E11C-4D39-90E8-938FB5A86045}"), "PDMpgMux.ax"}
      };
      foreach (var filter in filters)
      {
        try
        {
          if (!FilterGraphTools.IsThisComObjectInstalled(filter.Key))
          {
            var filterPath = FileUtils.BuildAssemblyRelativePath(filter.Value);
            COMRegistration.Register(filterPath, true);
          }
        }
        catch (Exception ex)
        {
          ServiceRegistration.Get<ILogger>().Error("SlimTvProxy: Failed to register filter {0}", ex, filter.Value);
        }
      }
    }

    protected override void PrepareProgramData()
    {
      base.PrepareProgramData();
      // TVE3 doesn't allow all kind of required modifications of paths yet, so we need to use some "old" paths here
      try
      {
        string mpTveServer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Team MediaPortal", "MediaPortal TV Server");
        string logFolder = Path.Combine(mpTveServer, "log");
        if (!Directory.Exists(logFolder))
          Directory.CreateDirectory(logFolder);


        string mpIpTvConfig = "MPIPTVSource.ini";
        string target = Path.Combine(mpTveServer, mpIpTvConfig);
        if (!File.Exists(target))
        {
          File.Copy(Path.Combine(ServiceRegistration.Get<IPathManager>().GetPath("<TVCORE>"), mpIpTvConfig), target);
        }
      }
      catch (Exception ex)
      {
        ServiceRegistration.Get<ILogger>().Error("SlimTvProxy: Error creating TVE3 folders", ex);
      }
    }

    protected override void InitTvCore()
    {
      if (!InitializeGentle())
      {
        DeInit();
        return;
      }

      // Handle events from TvEngine
      if (!RegisterEvents())
      {
        ServiceRegistration.Get<ILogger>().Error("SlimTvProxy: Failed to register events. This happens only if startup failed. Stopping plugin now.");
        DeInit();
      }
    }

    protected bool InitializeGentle()
    {
      try
      {
        // Use the same Gentle.config as the TVEngine
        string gentleConfigFile = Path.Combine(ServiceRegistration.Get<IPathManager>().GetPath("<TVCORE>"), "Gentle.config");
        // but be quiet when it doesn't exists, as not everyone has the TV Engine installed
        if (!File.Exists(gentleConfigFile))
        {
          ServiceRegistration.Get<ILogger>().Info("SlimTvProxy: Cannot find Gentle.config file, assuming TVEngine isn't installed...");
          return false;
        }
        Gentle.Common.Configurator.AddFileHandler(gentleConfigFile);

        //Load settings
        ISettingsManager settingsManager = ServiceRegistration.Get<ISettingsManager>();
        SlimTvProxySettings settings = settingsManager.Load<SlimTvProxySettings>();
        settingsManager.Save(settings);
        RemoteControl.HostName = settings.HostName;
        ProviderFactory.SetDefaultProviderConnectionString(settings.DatabaseConnectionString);
        ProviderFactory.SetDefaultProvider(settings.DatabaseProvider);

        _allCards = new List<Card>(Card.ListAll());
      }
      catch (Exception ex)
      {
        ServiceRegistration.Get<ILogger>().Error("SlimTvProxy: Failed to connect to TVEngine", ex);
        return false;
      }
      return true;
    }

    public override bool DeInit()
    {
      RemoteControl.Clear();
      lock (_recordingSync)
      {
        if(_checkForRecordingTimer != null) _checkForRecordingTimer.Dispose();
        _checkForRecordingTimer = null;
        if(_allCards != null) _allCards.Clear();
        _allCards = null;
        _currentlyRecording.Clear();
        _currentlyRecording = null;
      }
      return true;
    }

    #endregion

    #region Recordings / MediaLibrary synchronization

    private void CheckForRecordings(object oState)
    {
      lock (_recordingSync)
      {
        TvServerEventType eventType = TvServerEventType.RecordingStarted;
        if (_allCards != null)
        {
          foreach (Card card in _allCards)
          {
            if (card == null) continue;
            if (RemoteControl.Instance.CardPresent(card.IdCard) == false)
            {
              if (_currentlyRecording.ContainsKey(card))
                _currentlyRecording.Remove(card);
              continue;
            }

            bool cardRecording = false;
            var users = RemoteControl.Instance.GetUsersForCard(card.IdCard);
            if (users == null) continue;
            for (int i = 0; i < users.Count(); i++)
            {
              if (RemoteControl.Instance.IsRecording(ref users[i]) == true)
              {
                cardRecording = true;
                if (_currentlyRecording.ContainsKey(card) == false)
                {
                  _currentlyRecording.Add(card, RemoteControl.Instance.RecordingFileName(ref users[i]));
                  eventType = TvServerEventType.RecordingStarted;
                  if (File.Exists(_currentlyRecording[card]))
                  {
                    ServiceRegistration.Get<ILogger>().Info("SlimTvProxy: {0}: {1}", eventType, _currentlyRecording[card]);
                    ImportRecording(_currentlyRecording[card]);
                  }
                  break;
                }
              }
            }

            if (cardRecording == false)
            {
              if (_currentlyRecording.ContainsKey(card) == true)
              {
                eventType = TvServerEventType.RecordingEnded;
                ServiceRegistration.Get<ILogger>().Info("SlimTvProxy: {0}: {1}", eventType, _currentlyRecording[card]);
                ImportRecording(_currentlyRecording[card]);
                _currentlyRecording.Remove(card);
              }
            }

          }
        }
      }
    }

    protected override bool RegisterEvents()
    {
      _checkForRecordingTimer = new Timer(new TimerCallback(CheckForRecordings), null, _startDelay, _checkInterval);
      if (_checkForRecordingTimer == null)
        return false;

      return true;
    }

    protected override void OnTvServerEvent(object sender, EventArgs eventArgs)
    {
      try
      {
        TvServerEventArgs tvEvent = (TvServerEventArgs)eventArgs;

        if (tvEvent.EventType == TvServerEventType.RecordingStarted || tvEvent.EventType == TvServerEventType.RecordingEnded)
        {
          var recording = Recording.Retrieve(tvEvent.Recording.IdRecording);
          if (recording != null)
          {
            ServiceRegistration.Get<ILogger>().Info("SlimTvProxy: {0}: {1}", tvEvent.EventType, recording.FileName);
            ImportRecording(recording.FileName);
          }
        }
      }
      catch (Exception ex)
      {
        ServiceRegistration.Get<ILogger>().Warn("SlimTvProxy: Exception while handling TvServerEvent", ex);
      }
    }

    protected override bool GetRecordingConfiguration(out List<string> recordingFolders, out string singlePattern, out string seriesPattern)
    {
      singlePattern = string.Empty;
      seriesPattern = string.Empty;
      recordingFolders = new List<string>();

      if (_allCards == null)
        return false;

      // Get all different recording folders
      recordingFolders = _allCards.Select(c => c.RecordingFolder).Where(f => !string.IsNullOrEmpty(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

      TvBusinessLayer layer = new TvBusinessLayer();
      singlePattern = layer.GetSetting("moviesformat", string.Empty).Value;
      seriesPattern = layer.GetSetting("seriesformat", string.Empty).Value;
      return recordingFolders.Count > 0;
    }

    #endregion

    #region ITvProvider implementation

    private IUser GetUserByUserName(string userName)
    {
      return _allCards
        .Where(c => c != null && c.Enabled)
        .SelectMany(c => { var users = RemoteControl.Instance.GetUsersForCard(c.IdCard); return users ?? new IUser[] { }; })
        .FirstOrDefault(u => u.Name == userName);
    }

    public override bool StopTimeshift(string userName, int slotIndex)
    {
      IUser user;
      user = GetUserByUserName(GetUserName(userName, slotIndex));
      if (user == null)
        return false;
      return RemoteControl.Instance.StopTimeShifting(ref user);
    }

    public override MediaItem CreateMediaItem(int slotIndex, string streamUrl, IChannel channel)
    {
      // Channel is usually only passed as placeholder with ID only, so query the details here
      TvDatabase.Channel fullChannel = TvDatabase.Channel.Retrieve(channel.ChannelId);
      bool isTv = fullChannel.IsTv;
      return CreateMediaItem(slotIndex, streamUrl, channel, isTv, fullChannel.ToChannel());
    }

    public override bool GetNowNextProgram(IChannel channel, out IProgram programNow, out IProgram programNext)
    {
      var tvChannel = TvDatabase.Channel.Retrieve(channel.ChannelId);
      programNow = tvChannel.CurrentProgram.ToProgram();
      programNext = tvChannel.NextProgram.ToProgram();
      return programNow != null || programNext != null;
    }

    public override bool GetNowAndNextForChannelGroup(IChannelGroup channelGroup, out IDictionary<int, IProgram[]> nowNextPrograms)
    {
      nowNextPrograms = new Dictionary<int, IProgram[]>();
      IList<IChannel> channels;
      if (!GetChannels(channelGroup, out channels))
        return false;

      foreach (IChannel channel in channels)
      {
        IProgram programNow;
        IProgram programNext;
        if (GetNowNextProgram(channel, out programNow, out programNext))
          nowNextPrograms[channel.ChannelId] = new[] { programNow, programNext };
      }
      return true;
    }

    public override bool GetPrograms(IChannel channel, DateTime from, DateTime to, out IList<IProgram> programs)
    {
      programs = GetPrograms(TvDatabase.Channel.Retrieve(channel.ChannelId), from, to)
        .Select(tvProgram => tvProgram.ToProgram(true))
        .Distinct(ProgramComparer.Instance)
        .ToList();
      return programs.Count > 0;
    }

    private string GetDateTimeString()
    {
      string provider = Gentle.Framework.ProviderFactory.GetDefaultProvider().Name.ToLower();
      if (provider == "mysql") return "yyyy-MM-dd HH:mm:ss";
      return "yyyyMMdd HH:mm:ss";
    }

    public List<TvDatabase.Program> GetPrograms(TvDatabase.Channel channel, DateTime from, DateTime to)
    {
      IFormatProvider mmddFormat = new CultureInfo(String.Empty, false);
      SqlBuilder sb = new SqlBuilder(Gentle.Framework.StatementType.Select, typeof(TvDatabase.Program));
      sb.AddConstraint(Operator.Equals, "idChannel", channel.IdChannel);
      sb.AddConstraint(String.Format("startTime>='{0}'", from.ToString(GetDateTimeString(), mmddFormat)));
      sb.AddConstraint(String.Format("endTime<='{0}'", to.ToString(GetDateTimeString(), mmddFormat)));
      sb.AddOrderByField(true, "startTime");
      SqlStatement stmt = sb.GetStatement(true);
      IList programs = ObjectFactory.GetCollection(typeof(TvDatabase.Program), stmt.Execute());
      return (List<TvDatabase.Program>)programs;
    }

    public override bool GetPrograms(string title, DateTime from, DateTime to, out IList<IProgram> programs)
    {
      programs = SearchPrograms(title).Where(p => p.StartTime >= from && p.StartTime <= to || p.EndTime >= from && p.EndTime <= to)
        .Select(tvProgram => tvProgram.ToProgram(true))
        .Distinct(ProgramComparer.Instance)
        .ToList();
      return programs.Count > 0;
    }

    public List<TvDatabase.Program> SearchPrograms(string title)
    {
      IFormatProvider mmddFormat = new CultureInfo(String.Empty, false);
      SqlBuilder sb = new SqlBuilder(Gentle.Framework.StatementType.Select, typeof(TvDatabase.Program));
      sb.AddConstraint(Operator.Like, "title", title);
      sb.AddOrderByField(true, "startTime");
      SqlStatement stmt = sb.GetStatement(true);
      IList programs = ObjectFactory.GetCollection(typeof(TvDatabase.Program), stmt.Execute());
      return (List<TvDatabase.Program>)programs;
    }

    public override bool GetProgramsGroup(IChannelGroup channelGroup, DateTime from, DateTime to, out IList<IProgram> programs)
    {
      programs = new List<IProgram>();
      if (channelGroup.ChannelGroupId < 0)
      {
        foreach (var channel in GetRadioGuideChannelsForGroup(-channelGroup.ChannelGroupId))
          CollectionUtils.AddAll(programs, GetPrograms(TvDatabase.Channel.Retrieve(channel.IdChannel), from, to).Select(p => p.ToProgram()));
      }
      else
      {
        foreach (var channel in GetTVGuideChannelsForGroup(channelGroup.ChannelGroupId))
          CollectionUtils.AddAll(programs, GetPrograms(TvDatabase.Channel.Retrieve(channel.IdChannel), from, to).Select(p => p.ToProgram()));
      }
      return programs.Count > 0;
    }

    private List<TvDatabase.Channel> GetRadioGuideChannelsForGroup(int groupFilterId)
    {
      List<TvDatabase.Channel> refChannels = new List<TvDatabase.Channel>();
      IList<TvDatabase.ChannelGroup> groups = TvDatabase.ChannelGroup.ListAll();
      foreach (TvDatabase.ChannelGroup group in groups)
      {
        if (group.IdGroup == groupFilterId)
        {
          IList<GroupMap> maps = group.ReferringGroupMap();
          foreach (GroupMap map in maps)
          {
            TvDatabase.Channel channel = map.ReferencedChannel();
            if (channel != null && channel.IsRadio) refChannels.Add(channel);
          }
          break;
        }
      }
      return refChannels;
    }

    private List<TvDatabase.Channel> GetTVGuideChannelsForGroup(int groupFilterId)
    {
      List<TvDatabase.Channel> refChannels = new List<TvDatabase.Channel>();
      IList<TvDatabase.ChannelGroup> groups = TvDatabase.ChannelGroup.ListAll();
      foreach (TvDatabase.ChannelGroup group in groups)
      {
        if (group.IdGroup == groupFilterId)
        {
          IList<GroupMap> maps = group.ReferringGroupMap();
          foreach (GroupMap map in maps)
          {
            TvDatabase.Channel channel = map.ReferencedChannel();
            if (channel != null && channel.IsTv) refChannels.Add(channel);
          }
          break;
        }
      }
      return refChannels;
    }

    public override bool GetProgramsForSchedule(ISchedule schedule, out IList<IProgram> programs)
    {
      programs = new List<IProgram>();
      var tvSchedule = TvDatabase.Schedule.Retrieve(schedule.ScheduleId);
      if (tvSchedule == null)
        return false;

      programs = TvDatabase.Schedule.GetProgramsForSchedule(tvSchedule).Select(p => p.ToProgram()).ToList();
      return programs.Count > 0;
    }

    public override bool GetChannel(IProgram program, out IChannel channel)
    {
      channel = TvDatabase.Channel.Retrieve(program.ChannelId).ToChannel();
      return true;
    }

    public override bool GetProgram(int programId, out IProgram program)
    {
      program = TvDatabase.Program.Retrieve(programId).ToProgram();
      return program != null;
    }

    public override bool GetChannelGroups(out IList<IChannelGroup> groups)
    {
      groups = TvDatabase.ChannelGroup.ListAll()
        .OrderBy(tvGroup => tvGroup.SortOrder)
        .Select(tvGroup => tvGroup.ToChannelGroup())
        .Union(
          RadioChannelGroup.ListAll()
          .OrderBy(radioGroup => radioGroup.SortOrder)
          .Select(radioGroup => radioGroup.ToChannelGroup())
        )
        .ToList();
      return true;
    }

    public override bool GetChannel(int channelId, out IChannel channel)
    {
      channel = TvDatabase.Channel.Retrieve(channelId).ToChannel();
      return channel != null;
    }

    public override bool GetChannels(IChannelGroup group, out IList<IChannel> channels)
    {
      if (group.ChannelGroupId < 0)
      {
        var radioGroup = RadioChannelGroup.Retrieve(-group.ChannelGroupId);
        channels = GetChannelsInGroup(radioGroup.IdGroup)
          .OrderBy(c => c.SortOrder)
          .Where(c => c != null && c.VisibleInGuide)
          .Select(c => c.ToChannel())
          .ToList();
      }
      else
      {
        var tvGroup = TvDatabase.ChannelGroup.Retrieve(group.ChannelGroupId);
        channels = GetChannelsInGroup(tvGroup.IdGroup)
          // Bug? SortOrder contains logical channel number, not the group sort order?
          // .OrderBy(c => c.SortOrder)
          .Where(c => c != null && c.VisibleInGuide)
          .Select(c => c.ToChannel())
          .ToList();
      }
      return true;
    }

    private List<TvDatabase.Channel> GetChannelsInGroup(int groupId)
    {
      List<TvDatabase.Channel> refChannels = new List<TvDatabase.Channel>();
      IList<TvDatabase.ChannelGroup> groups = TvDatabase.ChannelGroup.ListAll();
      foreach (TvDatabase.ChannelGroup group in groups)
      {
        if (group.IdGroup == groupId)
        {
          IList<GroupMap> maps = group.ReferringGroupMap();
          foreach (GroupMap map in maps)
          {
            TvDatabase.Channel channel = map.ReferencedChannel();
            if (channel != null) refChannels.Add(channel);
          }
          break;
        }
      }
      return refChannels;
    }

    public override bool GetSchedules(out IList<ISchedule> schedules)
    {
      schedules = TvDatabase.Schedule.ListAll().Select(s => s.ToSchedule()).ToList();
      return true;
    }

    public override bool CreateSchedule(IProgram program, ScheduleRecordingType recordingType, out ISchedule schedule)
    {
      var tvProgram = TvDatabase.Program.Retrieve(program.ProgramId);
      if (tvProgram == null)
      {
        schedule = null;
        return false;
      }
      if (CreateProgram(tvProgram, (int)recordingType, out schedule))
      {
        RemoteControl.Instance.OnNewSchedule();
      }
      return schedule != null;
    }

    public static bool CreateProgram(TvDatabase.Program program, int scheduleType, out ISchedule currentSchedule)
    {
      ServiceRegistration.Get<ILogger>().Debug("SlimTvProxy.CreateProgram: program = {0}", program.ToString());
      TvDatabase.Schedule schedule;
      TvDatabase.Schedule saveSchedule = null;
      TvBusinessLayer layer = new TvBusinessLayer();
      if (IsRecordingProgram(program, out schedule, false)) // check if schedule is already existing
      {
        ServiceRegistration.Get<ILogger>().Debug("SlimTvProxy.CreateProgram - series schedule found ID={0}, Type={1}", schedule.IdSchedule, schedule.ScheduleType);
        ServiceRegistration.Get<ILogger>().Debug("                            - schedule= {0}", schedule.ToString());
        if (schedule.IsSerieIsCanceled(schedule.GetSchedStartTimeForProg(program), program.IdChannel))
        {
          // Delete the cancelled schedule.
          saveSchedule = schedule;
          schedule = new TvDatabase.Schedule(program.IdChannel, program.Title, program.StartTime, program.EndTime)
          {
            PreRecordInterval = saveSchedule.PreRecordInterval,
            PostRecordInterval = saveSchedule.PostRecordInterval,
            ScheduleType = (int)ScheduleRecordingType.Once
          };
        }
      }
      else
      {
        ServiceRegistration.Get<ILogger>().Debug("SlimTvProxy.CreateProgram - no series schedule");
        // No series schedule => create it
        schedule = new TvDatabase.Schedule(program.IdChannel, program.Title, program.StartTime, program.EndTime)
        {
          PreRecordInterval = Int32.Parse(layer.GetSetting("preRecordInterval", "5").Value),
          PostRecordInterval = Int32.Parse(layer.GetSetting("postRecordInterval", "5").Value),
          ScheduleType = scheduleType
        };
      }

      if (saveSchedule != null)
      {
        ServiceRegistration.Get<ILogger>().Debug("SlimTvProxy.CreateProgram - UnCancelSerie at {0}", program.StartTime);
        saveSchedule.UnCancelSerie(program.StartTime, program.IdChannel);
        saveSchedule.Persist();
        currentSchedule = saveSchedule.ToSchedule();
      }
      else
      {
        ServiceRegistration.Get<ILogger>().Debug("SlimTvProxy.CreateProgram - create schedule = {0}", schedule.ToString());
        schedule.Persist();
        currentSchedule = schedule.ToSchedule();
      }
      return currentSchedule != null;
    }

    public static bool IsRecordingProgram(TvDatabase.Program program, out TvDatabase.Schedule recordingSchedule, bool filterCanceledRecordings)
    {
      recordingSchedule = null;

      IList<TvDatabase.Schedule> schedules = TvDatabase.Schedule.ListAll();
      foreach (TvDatabase.Schedule schedule in schedules)
      {
        if (schedule.Canceled != TvDatabase.Schedule.MinSchedule || (filterCanceledRecordings && schedule.IsSerieIsCanceled(schedule.GetSchedStartTimeForProg(program), program.IdChannel)))
        {
          continue;
        }
        if (schedule.IsManual && schedule.IdChannel == program.IdChannel && schedule.EndTime >= program.EndTime)
        {
          TvDatabase.Schedule manual = schedule.Clone();
          manual.ProgramName = program.Title;
          manual.EndTime = program.EndTime;
          manual.StartTime = program.StartTime;
          if (manual.IsRecordingProgram(program, filterCanceledRecordings))
          {
            recordingSchedule = schedule;
            return true;
          }
        }
        else if (schedule.IsRecordingProgram(program, filterCanceledRecordings))
        {
          recordingSchedule = schedule;
          return true;
        }
      }
      return false;
    }

    public override bool CreateScheduleByTime(IChannel channel, DateTime from, DateTime to, ScheduleRecordingType recordingType, out ISchedule schedule)
    {
      TvBusinessLayer layer = new TvBusinessLayer();
      TvDatabase.Schedule tvSchedule = layer.AddSchedule(channel.ChannelId, "Manual", from, to, (int)recordingType);
      tvSchedule.PreRecordInterval = Int32.Parse(layer.GetSetting("preRecordInterval", "5").Value);
      tvSchedule.PostRecordInterval = Int32.Parse(layer.GetSetting("postRecordInterval", "5").Value);
      tvSchedule.Persist();
      RemoteControl.Instance.OnNewSchedule();
      schedule = tvSchedule.ToSchedule();
      return true;
    }

    public override bool RemoveScheduleForProgram(IProgram program, ScheduleRecordingType recordingType)
    {
      var canceledProgram = TvDatabase.Program.Retrieve(program.ProgramId);
      if (canceledProgram == null)
        return false;
      foreach (TvDatabase.Schedule schedule in TvDatabase.Schedule.ListAll().Where(schedule => schedule.IsRecordingProgram(canceledProgram, true)))
      {
        switch (schedule.ScheduleType)
        {
          case (int)ScheduleRecordingType.Once:
            schedule.Delete();
            RemoteControl.Instance.OnNewSchedule();
            break;
          default:
            CanceledSchedule canceledSchedule = new CanceledSchedule(schedule.IdSchedule, schedule.IdChannel, program.StartTime);
            canceledSchedule.Persist();
            RemoteControl.Instance.OnNewSchedule();
            break;
        }
      }
      return true;
    }

    public override bool RemoveSchedule(ISchedule schedule)
    {
      TvDatabase.Schedule tvSchedule = TvDatabase.Schedule.Retrieve(schedule.ScheduleId);
      // Already deleted somewhere else?
      if (tvSchedule == null)
        return true;
      RemoteControl.Instance.StopRecordingSchedule(tvSchedule.IdSchedule);
      // delete canceled schedules first
      foreach (var cs in CanceledSchedule.ListAll().Where(x => x.IdSchedule == tvSchedule.IdSchedule))
        cs.Remove();
      tvSchedule.Remove();
      RemoteControl.Instance.OnNewSchedule(); // I don't think this is needed, but doesn't hurt either
      return true;
    }

    public override bool GetRecordingStatus(IProgram program, out RecordingStatus recordingStatus)
    {
      var tvProgram = (IProgramRecordingStatus)TvDatabase.Program.Retrieve(program.ProgramId).ToProgram(true);
      recordingStatus = tvProgram.RecordingStatus;
      return true;
    }

    public override bool GetRecordingFileOrStream(IProgram program, out string fileOrStream)
    {
      fileOrStream = null;
      Recording recording;
      if (!GetRecording(program, out recording))
        return false;

      fileOrStream = recording.FileName; // FileName represents a local filesystem path on the server. It cannot be used directly in multiseat (RTSP required).
      return true;
    }

    private static bool GetRecording(IProgram program, out Recording recording)
    {
      recording = Recording.ListAllActive().FirstOrDefault(r => r.IsRecording && r.IdChannel == program.ChannelId && r.Title == program.Title);
      return recording != null;
    }

    private static bool GetRecording(string filename, out Recording recording)
    {
      recording = Recording.ListAllActive().FirstOrDefault(r => r.IsRecording && string.Equals(r.FileName, filename, StringComparison.OrdinalIgnoreCase));
      return recording != null;
    }

    protected override string SwitchTVServerToChannel(string userName, int channelId)
    {
      if (String.IsNullOrEmpty(userName))
      {
        ServiceRegistration.Get<ILogger>().Error("Called SwitchTVServerToChannel with empty userName");
        throw new ArgumentNullException("userName");
      }

      IUser currentUser = UserFactory.CreateBasicUser(userName, -1);
      ServiceRegistration.Get<ILogger>().Debug("Starting timeshifiting with username {0} on channel id {1}", userName, channelId);

      // actually start timeshifting
      VirtualCard card;
      TvResult result = RemoteControl.Instance.StartTimeShifting(ref currentUser, channelId, out card);
      // make sure result is correct and return
      if (result != TvResult.Succeeded)
      {
        ServiceRegistration.Get<ILogger>().Error("Starting timeshifting failed with result {0}", result);
        return null;
      }
      if (card == null)
      {
        ServiceRegistration.Get<ILogger>().Error("Couldn't get virtual card");
        return null;
      }

      if (File.Exists(card.TimeShiftFileName))
        return card.TimeShiftFileName;
      else
        return card.RTSPUrl;
    }

    protected IUser GetUserByUserName(string userName, bool create = false)
    {
      if (userName == null)
      {
        ServiceRegistration.Get<ILogger>().Warn("Used user with null name");
        return null;
      }

      if (!_tvUsers.ContainsKey(userName) && !create)
        return null;

      if (!_tvUsers.ContainsKey(userName) && create)
        _tvUsers.Add(userName, new User(userName, false));

      return _tvUsers[userName];
    }

    public override bool IsCurrentlyRecording(string fileName, out ISchedule schedule)
    {
      Recording recording;
      schedule = null;
      if (!GetRecording(fileName, out recording) || recording.Idschedule <= 0)
        return false;

      schedule = TvDatabase.Schedule.ListAll().FirstOrDefault(s => s.IdSchedule == recording.Idschedule).ToSchedule();
      return schedule != null;
    }

    #endregion
  }
}
