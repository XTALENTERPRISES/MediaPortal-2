#region Copyright (C) 2007-2012 Team MediaPortal

/*
    Copyright (C) 2007-2012 Team MediaPortal
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
using MediaPortal.Common;
using MediaPortal.Common.Localization;
using MediaPortal.Common.MediaManagement;
using MediaPortal.Common.ResourceAccess;
using MediaPortal.Plugins.SlimTvClient.Interfaces;
using MediaPortal.Plugins.SlimTvClient.Interfaces.Items;
using MediaPortal.UI.Players.Video;
using MediaPortal.UI.Players.Video.Tools;
using MediaPortal.UI.Presentation.Players;

namespace MediaPortal.Plugins.SlimTvClient
{
  public class LiveTvPlayer : TsVideoPlayer, IUIContributorPlayer//, IReusablePlayer
  {
    #region Variables

    protected IList<ITimeshiftContext> _timeshiftContexes;
    protected StreamInfoHandler _chapterInfo = null;

    #endregion

    #region ctor

    /// <summary>
    /// Constructs a LiveTvPlayer player object.
    /// </summary>
    public LiveTvPlayer()
    {
      PlayerTitle = "LiveTvPlayer"; // for logging
    }

    #endregion

    #region IUIContributorPlayer Member

    public Type UIContributorType
    {
      get { return typeof(SlimTvUIContributor); }
    }

    #endregion

    public ITimeshiftContext CurrentTimeshiftContext
    {
      get
      {
        int index;
        return GetContextIndex(CurrentTime, out index) ? _timeshiftContexes[index] : null;
      }
    }

    private TimeSpan GetStartDuration(int chapterIndex)
    {
      return _timeshiftContexes[chapterIndex].TuneInTime - _timeshiftContexes[0].TuneInTime;
    }

    private bool GetContextIndex(TimeSpan timeSpan, out int index)
    {
      lock (SyncObj)
        if (_chapterInfo == null)
          EnumerateChapters();

      TimeSpan totalTime = new TimeSpan();
      index = 0;
      foreach (ITimeshiftContext timeshiftContext in _timeshiftContexes)
      {
        if (timeSpan >= totalTime &&
          (
          (timeSpan <= totalTime + timeshiftContext.TimeshiftDuration) ||
          timeshiftContext.TimeshiftDuration.TotalSeconds == 0 /* currently playing */
          ))
          return true;

        index++;
        totalTime += timeshiftContext.TimeshiftDuration;
      }
      return false;
    }

    private TimeSpan GetContextStart(int index)
    {
      lock (SyncObj)
        if (_chapterInfo == null)
          EnumerateChapters();

      TimeSpan totalTime = new TimeSpan();
      int i = 0;
      foreach (ITimeshiftContext timeshiftContext in _timeshiftContexes)
      {
        if (i >= index)
          break;
        i++;
        totalTime += timeshiftContext.TimeshiftDuration;
      }
      return totalTime;
    }

    protected override void EnumerateChapters()
    {
      lock (SyncObj)
        _chapterInfo = new StreamInfoHandler();

      IPlayerContextManager playerContextManager = ServiceRegistration.Get<IPlayerContextManager>();
      for (int index = 0; index < playerContextManager.NumActivePlayerContexts; index++)
      {
        IPlayerContext playerContext = playerContextManager.GetPlayerContext(index);
        if (playerContext == null || playerContext.CurrentPlayer != this)
          continue;

        LiveTvMediaItem liveTvMediaItem = playerContext.CurrentMediaItem as LiveTvMediaItem;
        if (liveTvMediaItem == null)
          continue;

        _timeshiftContexes = liveTvMediaItem.TimeshiftContexes;
        int i = 0;
        foreach (ITimeshiftContext timeshiftContext in _timeshiftContexes)
        {
          string program = timeshiftContext.Program != null ? timeshiftContext.Program.Title :
            ServiceRegistration.Get<ILocalization>().ToString("[SlimTvClient.NoProgram]");

          _chapterInfo.AddUnique(new StreamInfo(null, i++, string.Format("{0}: {1}", timeshiftContext.Channel.Name, program), 0));
        }
      }
      // Enumeration can happen before a player gets active, so ensure a valid list of ITimeshiftContext
      if (_timeshiftContexes == null)
        _timeshiftContexes = new List<ITimeshiftContext>();
    }

    #region IChapterPlayer overrides

    public override string[] Chapters
    {
      get
      {
        EnumerateChapters();
        return _chapterInfo.Count == 0 ? EMPTY_STRING_ARRAY : _chapterInfo.GetStreamNames();
      }
    }

    public override void SetChapter(string chapter)
    {
      lock (SyncObj)
      {
        if (_chapterInfo != null)
        {
          StreamInfo chapterInfo = _chapterInfo.FindStream(chapter);
          if (chapterInfo != null)
            CurrentTime = GetStartDuration(chapterInfo.StreamIndex);
        }
      }
    }

    public override bool ChaptersAvailable
    {
      get
      {
        lock (SyncObj)
        {
          EnumerateChapters();
          return _chapterInfo.Count > 1;
        }
      }
    }

    public override void NextChapter()
    {
      int index;
      if (GetContextIndex(CurrentTime, out index))
        if (index < _chapterInfo.Count - 1)
          CurrentTime = GetContextStart(index + 1);
    }

    public override void PrevChapter()
    {
      int index;
      if (GetContextIndex(CurrentTime, out index))
        if (index > 0)
          CurrentTime = GetContextStart(index - 1);
    }

    public override string CurrentChapter
    {
      get
      {
        int index;
        return GetContextIndex(CurrentTime, out index) ? _chapterInfo[index].Name : string.Empty;
      }
    }

    #endregion

    #region IReusablePlayer Member

    public event RequestNextItemDlgt NextItemRequest;

    public bool NextItem(MediaItem mediaItem, StartTime startTime)
    {
      string mimeType;
      string title;
      if (!mediaItem.GetPlayData(out mimeType, out title))
        return false;
      if (mimeType != "video/livetv")
        return false;
      IResourceLocator locator = mediaItem.GetResourceLocator();
      if (locator == null)
        return false;
      Shutdown();
      SetMediaItem(locator, title);
      return true;
    }

    #endregion
  }
}