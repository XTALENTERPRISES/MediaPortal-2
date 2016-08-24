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

using MediaPortal.Common;
using MediaPortal.Common.FanArt;
using MediaPortal.Common.Localization;
using MediaPortal.Common.MediaManagement.DefaultItemAspects;
using MediaPortal.Common.MediaManagement.Helpers;
using MediaPortal.Common.PathManager;
using MediaPortal.Common.Threading;
using MediaPortal.Extensions.OnlineLibraries.Libraries.Common;
using MediaPortal.Extensions.OnlineLibraries.Libraries.Common.Data;
using MediaPortal.Extensions.OnlineLibraries.Matches;
using MediaPortal.Extensions.OnlineLibraries.Wrappers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace MediaPortal.Extensions.OnlineLibraries.Matchers
{
  public abstract class SeriesMatcher<TImg, TLang> : BaseMatcher<SeriesMatch, string>
  {
    public class SeriresMatcherSettings
    {
      public string LastRefresh { get; set; }
    }

    #region Init

    public SeriesMatcher(string cachePath, TimeSpan maxCacheDuration)
    {
      _cachePath = cachePath;
      _matchesSettingsFile = Path.Combine(cachePath, "SeriesMatches.xml");
      _maxCacheDuration = maxCacheDuration;

      _actorMatcher = new SimpleNameMatcher(Path.Combine(cachePath, "ActorMatches.xml"));
      _directorMatcher = new SimpleNameMatcher(Path.Combine(cachePath, "DirectorMatches.xml"));
      _writerMatcher = new SimpleNameMatcher(Path.Combine(cachePath, "WriterMatches.xml"));
      _characterMatcher = new SimpleNameMatcher(Path.Combine(cachePath, "CharacterMatches.xml"));
      _companyMatcher = new SimpleNameMatcher(Path.Combine(cachePath, "CompanyMatches.xml"));
      _networkMatcher = new SimpleNameMatcher(Path.Combine(cachePath, "NetworkMatches.xml"));
      _seriesNameMatcher = new SimpleNameMatcher(Path.Combine(cachePath, "SeriesNameMatches.xml"));
      _configFile = Path.Combine(cachePath, "SeriesConfig.xml");

      Init();
    }

    public override bool Init()
    {
      if (_wrapper != null)
        return true;

      if (!base.Init())
        return false;

      LoadConfig();

      return InitWrapper(UseSecureWebCommunication);
    }

    public abstract bool InitWrapper(bool useHttps);

    private void LoadConfig()
    {
      _config = Settings.Load<SeriresMatcherSettings>(_configFile);
      if (_config == null)
        _config = new SeriresMatcherSettings();
    }

    private void SaveConfig()
    {
      Settings.Save(_configFile, _config);
    }

    #endregion

    #region Constants

    public static string FANART_CACHE_PATH = ServiceRegistration.Get<IPathManager>().GetPath(@"<DATA>\FanArt\");

    protected override string MatchesSettingsFile
    {
      get { return _matchesSettingsFile; }
    }

    #endregion

    #region Fields

    private DateTime _memoryCacheInvalidated = DateTime.MinValue;
    private ConcurrentDictionary<string, SeriesInfo> _memoryCache = new ConcurrentDictionary<string, SeriesInfo>(StringComparer.OrdinalIgnoreCase);
    private ConcurrentDictionary<string, EpisodeInfo> _memoryCacheEpisode = new ConcurrentDictionary<string, EpisodeInfo>(StringComparer.OrdinalIgnoreCase);
    private SeriresMatcherSettings _config = new SeriresMatcherSettings();
    private string _cachePath;
    private string _matchesSettingsFile;
    private string _configFile;
    private TimeSpan _maxCacheDuration;
    private SimpleNameMatcher _companyMatcher;
    private SimpleNameMatcher _networkMatcher;
    private SimpleNameMatcher _actorMatcher;
    private SimpleNameMatcher _directorMatcher;
    private SimpleNameMatcher _writerMatcher;
    private SimpleNameMatcher _characterMatcher;
    private SimpleNameMatcher _seriesNameMatcher;

    protected ApiWrapper<TImg, TLang> _wrapper = null;

    #endregion

    #region External match storage

    public void StoreActorMatch(PersonInfo person)
    {
      string id;
      if (GetPersonId(person, out id))
        _actorMatcher.StoreNameMatch(id, person.Name, person.Name);
    }

    public void StoreDirectorMatch(PersonInfo person)
    {
      string id;
      if (GetPersonId(person, out id))
        _directorMatcher.StoreNameMatch(id, person.Name, person.Name);
    }

    public void StoreWriterMatch(PersonInfo person)
    {
      string id;
      if (GetPersonId(person, out id))
        _writerMatcher.StoreNameMatch(id, person.Name, person.Name);
    }

    public void StoreCharacterMatch(CharacterInfo character)
    {
      string id;
      if (GetCharacterId(character, out id))
        _characterMatcher.StoreNameMatch(id, character.Name, character.Name);
    }

    public void StoreCompanyMatch(CompanyInfo company)
    {
      string id;
      if (GetCompanyId(company, out id))
        _companyMatcher.StoreNameMatch(id, company.Name, company.Name);
    }

    public void StoreTvNetworkMatch(CompanyInfo company)
    {
      string id;
      if (GetCompanyId(company, out id))
        _networkMatcher.StoreNameMatch(id, company.Name, company.Name);
    }

    #endregion

    #region Metadata updaters

    /// <summary>
    /// Tries to lookup the Episode online and downloads images.
    /// </summary>
    /// <param name="episodeInfo">Episode to check</param>
    /// <returns><c>true</c> if successful</returns>
    public virtual bool FindAndUpdateEpisode(EpisodeInfo episodeInfo, bool forceQuickMode)
    {
      try
      {
        // Try online lookup
        if (!Init())
          return false;

        EpisodeInfo episodeMatch = null;
        SeriesInfo seriesMatch = null;
        SeriesInfo episodeSeries = episodeInfo.CloneBasicInstance<SeriesInfo>();
        string seriesId = null;
        string episodeId = null;
        string altEpisodeId = null;
        bool matchFound = false;
        bool seriesMatchFound = false;
        TLang language = FindBestMatchingLanguage(episodeInfo.Languages);

        if (GetSeriesId(episodeSeries, out seriesId))
        {
          seriesMatchFound = true;

          // Prefer memory cache
          CheckCacheAndRefresh();
          if (_memoryCache.TryGetValue(seriesId, out seriesMatch))
          {
            if (episodeInfo.SeriesName.IsEmpty)
              episodeInfo.SeriesName = seriesMatch.SeriesName;
          }
        }

        if (seriesId != null && episodeInfo.SeasonNumber.HasValue && episodeInfo.EpisodeNumbers.Count > 0)
        {
          altEpisodeId = seriesId + "|" + episodeInfo.SeasonNumber.Value + "|" + episodeInfo.EpisodeNumbers[0];
        }
        if (GetSeriesEpisodeId(episodeInfo, out episodeId))
        {
          // Prefer memory cache
          CheckCacheAndRefresh();
          if (_memoryCacheEpisode.TryGetValue(episodeId, out episodeMatch))
            matchFound = true;
          else if (altEpisodeId != null && _memoryCacheEpisode.TryGetValue(altEpisodeId, out episodeMatch))
            matchFound = true;
        }

        if (!matchFound)
        {
          // Load cache or create new list
          List<SeriesMatch> matches = _storage.GetMatches();

          // Use cached values before doing online query
          SeriesMatch match = matches.Find(m =>
            (string.Equals(m.ItemName, episodeSeries.SeriesName.ToString(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.OnlineName, episodeSeries.SeriesName.ToString(), StringComparison.OrdinalIgnoreCase)) &&
            (episodeSeries.FirstAired.HasValue && m.Year == episodeSeries.FirstAired.Value.Year || !episodeSeries.FirstAired.HasValue || m.Year == 0));
          Logger.Debug(GetType().Name + ": Try to lookup series \"{0}\" from cache: {1}", episodeSeries, match != null && !string.IsNullOrEmpty(match.Id));

          episodeMatch = CloneProperties(episodeInfo);
          if (match != null)
          {
            if (SetSeriesId(episodeMatch, match.Id))
            {
              seriesMatchFound = true;
            }
            else if (string.IsNullOrEmpty(seriesId))
            {
              //Match was found but with invalid Id probably to avoid a retry
              //No Id is available so online search will probably fail again
              return false;
            }
          }

          if (seriesMatchFound)
          {
            //If Id was found in cache the online movie info is probably also in the cache
            if (_wrapper.UpdateFromOnlineSeriesEpisode(episodeMatch, language, true))
            {
              Logger.Debug(GetType().Name + ": Found episode {0} in cache", episodeInfo.ToString());
              matchFound = true;
            }
          }

          if (!matchFound && !forceQuickMode)
          {
            Logger.Debug(GetType().Name + ": Search for episode {0} online", episodeInfo.ToString());

            //Try to update movie information from online source if online Ids are present
            if (!_wrapper.UpdateFromOnlineSeriesEpisode(episodeMatch, language, false))
            {
              //Search for the movie online and update the Ids if a match is found
              if (_wrapper.SearchSeriesEpisodeUniqueAndUpdate(episodeMatch, language))
              {
                //Ids were updated now try to update movie information from online source
                if (_wrapper.UpdateFromOnlineSeriesEpisode(episodeMatch, language, false))
                  matchFound = true;
              }
            }
            else
            {
              matchFound = true;
            }
          }
        }

        //Always save match even if none to avoid retries
        SeriesInfo cloneBasicSeries = episodeMatch != null ? episodeMatch.CloneBasicInstance<SeriesInfo>() : null;
        StoreSeriesMatch(episodeSeries, cloneBasicSeries);

        if (matchFound && episodeMatch != null)
        {
          episodeInfo.HasChanged |= MetadataUpdater.SetOrUpdateId(ref episodeInfo.ImdbId, episodeMatch.ImdbId);
          episodeInfo.HasChanged |= MetadataUpdater.SetOrUpdateId(ref episodeInfo.MovieDbId, episodeMatch.MovieDbId);
          episodeInfo.HasChanged |= MetadataUpdater.SetOrUpdateId(ref episodeInfo.TvdbId, episodeMatch.TvdbId);
          episodeInfo.HasChanged |= MetadataUpdater.SetOrUpdateId(ref episodeInfo.TvMazeId, episodeMatch.TvMazeId);
          episodeInfo.HasChanged |= MetadataUpdater.SetOrUpdateId(ref episodeInfo.TvRageId, episodeMatch.TvRageId);

          episodeInfo.HasChanged |= MetadataUpdater.SetOrUpdateId(ref episodeInfo.SeriesImdbId, episodeMatch.SeriesImdbId);
          episodeInfo.HasChanged |= MetadataUpdater.SetOrUpdateId(ref episodeInfo.SeriesMovieDbId, episodeMatch.SeriesMovieDbId);
          episodeInfo.HasChanged |= MetadataUpdater.SetOrUpdateId(ref episodeInfo.SeriesTvdbId, episodeMatch.SeriesTvdbId);
          episodeInfo.HasChanged |= MetadataUpdater.SetOrUpdateId(ref episodeInfo.SeriesTvMazeId, episodeMatch.SeriesTvMazeId);
          episodeInfo.HasChanged |= MetadataUpdater.SetOrUpdateId(ref episodeInfo.SeriesTvRageId, episodeMatch.SeriesTvRageId);

          episodeInfo.HasChanged |= MetadataUpdater.SetOrUpdateString(ref episodeInfo.EpisodeName, episodeMatch.EpisodeName);
          episodeInfo.HasChanged |= MetadataUpdater.SetOrUpdateString(ref episodeInfo.Summary, episodeMatch.Summary);
          episodeInfo.HasChanged |= MetadataUpdater.SetOrUpdateString(ref episodeInfo.SeriesName, episodeMatch.SeriesName);

          episodeInfo.HasChanged |= MetadataUpdater.SetOrUpdateValue(ref episodeInfo.FirstAired, episodeMatch.FirstAired);
          episodeInfo.HasChanged |= MetadataUpdater.SetOrUpdateValue(ref episodeInfo.SeasonNumber, episodeMatch.SeasonNumber);
          MetadataUpdater.SetOrUpdateValue(ref episodeInfo.SeriesFirstAired, episodeMatch.SeriesFirstAired);

          episodeInfo.HasChanged |= MetadataUpdater.SetOrUpdateRatings(ref episodeInfo.Rating, episodeMatch.Rating);

          episodeInfo.HasChanged |= MetadataUpdater.SetOrUpdateList(episodeInfo.EpisodeNumbers, episodeMatch.EpisodeNumbers, true);
          episodeInfo.HasChanged |= MetadataUpdater.SetOrUpdateList(episodeInfo.DvdEpisodeNumbers, episodeMatch.DvdEpisodeNumbers, true);

          episodeInfo.HasChanged |= MetadataUpdater.SetOrUpdateList(episodeInfo.Genres, episodeMatch.Genres, true);

          //These lists contain Ids and other properties that are not persisted, so they will always appear changed.
          //So changes to these lists will only be stored if something else has changed.
          MetadataUpdater.SetOrUpdateList(episodeInfo.Actors, episodeMatch.Actors, true);
          MetadataUpdater.SetOrUpdateList(episodeInfo.Characters, episodeMatch.Characters, true);
          MetadataUpdater.SetOrUpdateList(episodeInfo.Directors, episodeMatch.Directors, true);
          MetadataUpdater.SetOrUpdateList(episodeInfo.Writers, episodeMatch.Writers, true);

          //Store person matches
          foreach (PersonInfo person in episodeInfo.Actors)
          {
            string id;
            if (GetPersonId(person, out id))
              _actorMatcher.StoreNameMatch(id, person.Name, person.Name);
          }
          foreach (PersonInfo person in episodeInfo.Directors)
          {
            string id;
            if (GetPersonId(person, out id))
              _directorMatcher.StoreNameMatch(id, person.Name, person.Name);
          }
          foreach (PersonInfo person in episodeInfo.Writers)
          {
            string id;
            if (GetPersonId(person, out id))
              _writerMatcher.StoreNameMatch(id, person.Name, person.Name);
          }

          //Store character matches
          foreach (CharacterInfo character in episodeInfo.Characters)
          {
            string id;
            if (GetCharacterId(character, out id))
              _characterMatcher.StoreNameMatch(id, character.Name, character.Name);
          }

          if (GetSeriesId(episodeInfo.CloneBasicInstance<SeriesInfo>(), out seriesId))
          {
            _memoryCache.TryAdd(seriesId, episodeInfo.CloneBasicInstance<SeriesInfo>());

            if (GetSeriesEpisodeId(episodeInfo, out episodeId))
            {
              _memoryCacheEpisode.TryAdd(episodeId, episodeInfo);
            }
            else
            {
              if (episodeInfo.SeasonNumber.HasValue && episodeInfo.EpisodeNumbers.Count > 0)
              {
                seriesId += "|" + episodeInfo.SeasonNumber.Value + "|" + episodeInfo.EpisodeNumbers[0];

                _memoryCacheEpisode.TryAdd(seriesId, episodeInfo);
              }
            }
          }

          return true;
        }

        return false;
      }
      catch (Exception ex)
      {
        Logger.Debug(GetType().Name + ": Exception while processing episode {0}", ex, episodeInfo.ToString());
        return false;
      }
    }

    public virtual bool UpdateSeries(SeriesInfo seriesInfo, bool updateEpisodeList, bool forceQuickMode)
    {
      try
      {
        // Try online lookup
        if (!Init())
          return false;

        string id;
        if (!GetSeriesId(seriesInfo, out id))
        {
          if (_seriesNameMatcher.GetNameMatch(seriesInfo.SeriesName.Text, out id))
          {
            if (!SetSeriesId(seriesInfo, id))
            {
              //Match probably stored with invalid Id to avoid retries. 
              //Searching for this series by name only failed so stop trying.
              return false;
            }
          }
        }

        TLang language = FindBestMatchingLanguage(seriesInfo.Languages);
        bool updated = false;
        SeriesInfo seriesMatch = CloneProperties(seriesInfo);
        seriesMatch.Seasons.Clear();
        seriesMatch.Episodes.Clear();
        //Try updating from cache
        if (!_wrapper.UpdateFromOnlineSeries(seriesMatch, language, true))
        {
          if (!forceQuickMode)
          {
            Logger.Debug(GetType().Name + ": Search for series {0} online", seriesInfo.ToString());

            //Try to update series information from online source if online Ids are present
            if (!_wrapper.UpdateFromOnlineSeries(seriesMatch, language, false))
            {
              //Search for the series online and update the Ids if a match is found
              if (_wrapper.SearchSeriesUniqueAndUpdate(seriesMatch, language))
              {
                //Ids were updated now try to fetch the online series info
                if (_wrapper.UpdateFromOnlineSeries(seriesMatch, language, false))
                  updated = true;
              }
            }
            else
            {
              updated = true;
            }
          }
        }
        else
        {
          Logger.Debug(GetType().Name + ": Found series {0} in cache", seriesInfo.ToString());
          updated = true;
        }

        if (updated)
        {
          //Reset next episode data because it was already aired
          if (seriesInfo.NextEpisodeAirDate.HasValue && seriesInfo.NextEpisodeAirDate.Value < DateTime.Now)
          {
            seriesInfo.NextEpisodeAirDate = null;
            seriesInfo.NextEpisodeNumber = null;
            seriesInfo.NextEpisodeSeasonNumber = null;
            seriesInfo.NextEpisodeName = null;
            seriesInfo.HasChanged = true;
          }

          seriesInfo.HasChanged |= MetadataUpdater.SetOrUpdateId(ref seriesInfo.TvdbId, seriesMatch.TvdbId);
          seriesInfo.HasChanged |= MetadataUpdater.SetOrUpdateId(ref seriesInfo.ImdbId, seriesMatch.ImdbId);
          seriesInfo.HasChanged |= MetadataUpdater.SetOrUpdateId(ref seriesInfo.MovieDbId, seriesMatch.MovieDbId);
          seriesInfo.HasChanged |= MetadataUpdater.SetOrUpdateId(ref seriesInfo.TvMazeId, seriesMatch.TvMazeId);
          seriesInfo.HasChanged |= MetadataUpdater.SetOrUpdateId(ref seriesInfo.TvRageId, seriesMatch.TvRageId);

          seriesInfo.HasChanged |= MetadataUpdater.SetOrUpdateString(ref seriesInfo.SeriesName, seriesMatch.SeriesName);
          seriesInfo.HasChanged |= MetadataUpdater.SetOrUpdateString(ref seriesInfo.OriginalName, seriesMatch.OriginalName);
          seriesInfo.HasChanged |= MetadataUpdater.SetOrUpdateString(ref seriesInfo.Description, seriesMatch.Description);
          seriesInfo.HasChanged |= MetadataUpdater.SetOrUpdateString(ref seriesInfo.Certification, seriesMatch.Certification);
          seriesInfo.HasChanged |= MetadataUpdater.SetOrUpdateString(ref seriesInfo.NextEpisodeName, seriesMatch.NextEpisodeName);

          if (seriesInfo.TotalSeasons < seriesMatch.TotalSeasons)
            seriesInfo.HasChanged = true;
          MetadataUpdater.SetOrUpdateValue(ref seriesInfo.TotalSeasons, seriesMatch.TotalSeasons);

          if (seriesInfo.TotalEpisodes < seriesMatch.TotalEpisodes)
            seriesInfo.HasChanged = true;
          MetadataUpdater.SetOrUpdateValue(ref seriesInfo.TotalEpisodes, seriesMatch.TotalEpisodes);

          seriesInfo.HasChanged |= MetadataUpdater.SetOrUpdateValue(ref seriesInfo.FirstAired, seriesMatch.FirstAired);
          seriesInfo.HasChanged |= MetadataUpdater.SetOrUpdateValue(ref seriesInfo.Popularity, seriesMatch.Popularity);
          seriesInfo.HasChanged |= MetadataUpdater.SetOrUpdateValue(ref seriesInfo.IsEnded, seriesMatch.IsEnded);
          seriesInfo.HasChanged |= MetadataUpdater.SetOrUpdateValue(ref seriesInfo.NextEpisodeAirDate, seriesMatch.NextEpisodeAirDate);
          seriesInfo.HasChanged |= MetadataUpdater.SetOrUpdateValue(ref seriesInfo.NextEpisodeNumber, seriesMatch.NextEpisodeNumber);
          seriesInfo.HasChanged |= MetadataUpdater.SetOrUpdateValue(ref seriesInfo.NextEpisodeSeasonNumber, seriesMatch.NextEpisodeSeasonNumber);
          seriesInfo.HasChanged |= MetadataUpdater.SetOrUpdateValue(ref seriesInfo.Score, seriesMatch.Score);

          seriesInfo.HasChanged |= MetadataUpdater.SetOrUpdateRatings(ref seriesInfo.Rating, seriesMatch.Rating);

          seriesInfo.HasChanged |= MetadataUpdater.SetOrUpdateList(seriesInfo.Genres, seriesMatch.Genres, true);
          seriesInfo.HasChanged |= MetadataUpdater.SetOrUpdateList(seriesInfo.Awards, seriesMatch.Awards, true);

          //These lists contain Ids and other properties that are not persisted, so they will always appear changed.
          //So changes to these lists will only be stored if something else has changed.
          MetadataUpdater.SetOrUpdateList(seriesInfo.Networks, seriesMatch.Networks, true);
          MetadataUpdater.SetOrUpdateList(seriesInfo.ProductionCompanies, seriesMatch.ProductionCompanies, true);
          MetadataUpdater.SetOrUpdateList(seriesInfo.Actors, seriesMatch.Actors, true);
          MetadataUpdater.SetOrUpdateList(seriesInfo.Characters, seriesMatch.Characters, true);

          MetadataUpdater.SetOrUpdateList(seriesInfo.Seasons, seriesMatch.Seasons, true);

          if (updateEpisodeList) //Comparing all episodes can be quite time consuming
            MetadataUpdater.SetOrUpdateList(seriesInfo.Episodes, seriesMatch.Episodes, true);

          //Store person matches
          foreach (PersonInfo person in seriesInfo.Actors)
          {
            if (GetPersonId(person, out id))
              _actorMatcher.StoreNameMatch(id, person.Name, person.Name);
          }

          //Store character matches
          foreach (CharacterInfo character in seriesInfo.Characters)
          {
            if (GetCharacterId(character, out id))
              _characterMatcher.StoreNameMatch(id, character.Name, character.Name);
          }

          //Store company matches
          foreach (CompanyInfo company in seriesInfo.ProductionCompanies)
          {
            if (GetCompanyId(company, out id))
              _companyMatcher.StoreNameMatch(id, company.Name, company.Name);
          }

          //Store network matches
          foreach (CompanyInfo company in seriesInfo.Networks)
          {
            if (GetCompanyId(company, out id))
              _networkMatcher.StoreNameMatch(id, company.Name, company.Name);
          }
        }

        string Id;
        if (!GetSeriesId(seriesInfo, out Id))
        {
          //Store empty match so it is not retried
          _seriesNameMatcher.StoreNameMatch("", seriesInfo.SeriesName.Text, seriesInfo.SeriesName.Text);
        }

        return updated;
      }
      catch (Exception ex)
      {
        Logger.Debug(GetType().Name + ": Exception while processing series {0}", ex, seriesInfo.ToString());
        return false;
      }
    }

    public virtual bool UpdateSeason(SeasonInfo seasonInfo, bool forceQuickMode)
    {
      try
      {
        // Try online lookup
        if (!Init())
          return false;

        TLang language = FindBestMatchingLanguage(seasonInfo.Languages);
        bool updated = false;
        SeasonInfo seasonMatch = CloneProperties(seasonInfo);
        //Try updating from cache
        if (!_wrapper.UpdateFromOnlineSeriesSeason(seasonMatch, language, true))
        {
          if (!forceQuickMode)
          {
            Logger.Debug(GetType().Name + ": Search for season {0} online", seasonInfo.ToString());

            //Try to update season information from online source
            if (_wrapper.UpdateFromOnlineSeriesSeason(seasonMatch, language, false))
              updated = true;
          }
        }
        else
        {
          Logger.Debug(GetType().Name + ": Found season {0} in cache", seasonInfo.ToString());
          updated = true;
        }

        if (updated)
        {
          seasonInfo.HasChanged |= MetadataUpdater.SetOrUpdateId(ref seasonInfo.TvdbId, seasonMatch.TvdbId);
          seasonInfo.HasChanged |= MetadataUpdater.SetOrUpdateId(ref seasonInfo.ImdbId, seasonMatch.ImdbId);
          seasonInfo.HasChanged |= MetadataUpdater.SetOrUpdateId(ref seasonInfo.MovieDbId, seasonMatch.MovieDbId);
          seasonInfo.HasChanged |= MetadataUpdater.SetOrUpdateId(ref seasonInfo.TvMazeId, seasonMatch.TvMazeId);
          seasonInfo.HasChanged |= MetadataUpdater.SetOrUpdateId(ref seasonInfo.TvRageId, seasonMatch.TvRageId);

          seasonInfo.HasChanged |= MetadataUpdater.SetOrUpdateId(ref seasonInfo.SeriesImdbId, seasonMatch.SeriesImdbId);
          seasonInfo.HasChanged |= MetadataUpdater.SetOrUpdateId(ref seasonInfo.SeriesMovieDbId, seasonMatch.SeriesMovieDbId);
          seasonInfo.HasChanged |= MetadataUpdater.SetOrUpdateId(ref seasonInfo.SeriesTvdbId, seasonMatch.SeriesTvdbId);
          seasonInfo.HasChanged |= MetadataUpdater.SetOrUpdateId(ref seasonInfo.SeriesTvMazeId, seasonMatch.SeriesTvMazeId);
          seasonInfo.HasChanged |= MetadataUpdater.SetOrUpdateId(ref seasonInfo.SeriesTvRageId, seasonMatch.SeriesTvRageId);

          seasonInfo.HasChanged |= MetadataUpdater.SetOrUpdateString(ref seasonInfo.SeriesName, seasonMatch.SeriesName);
          seasonInfo.HasChanged |= MetadataUpdater.SetOrUpdateString(ref seasonInfo.Description, seasonMatch.Description);

          if (seasonInfo.TotalEpisodes < seasonMatch.TotalEpisodes)
            seasonInfo.HasChanged |= MetadataUpdater.SetOrUpdateValue(ref seasonInfo.TotalEpisodes, seasonMatch.TotalEpisodes);

          seasonInfo.HasChanged |= MetadataUpdater.SetOrUpdateValue(ref seasonInfo.FirstAired, seasonMatch.FirstAired);
          seasonInfo.HasChanged |= MetadataUpdater.SetOrUpdateValue(ref seasonInfo.SeasonNumber, seasonMatch.SeasonNumber);
        }

        return updated;
      }
      catch (Exception ex)
      {
        Logger.Debug(GetType().Name + ": Exception while processing season {0}", ex, seasonInfo.ToString());
        return false;
      }
    }

    public virtual bool UpdateSeriesPersons(SeriesInfo seriesInfo, string occupation, bool forceQuickMode)
    {
      try
      {
        // Try online lookup
        if (!Init())
          return false;

        TLang language = FindBestMatchingLanguage(seriesInfo.Languages);
        bool updated = false;
        SeriesInfo seriesMatch = CloneProperties(seriesInfo);
        List<PersonInfo> persons = new List<PersonInfo>();
        if (occupation == PersonAspect.OCCUPATION_ACTOR)
        {
          foreach (PersonInfo person in seriesMatch.Actors)
          {
            string id;
            if (_actorMatcher.GetNameMatch(person.Name, out id))
            {
              if (SetPersonId(person, id))
              {
                //Only add if Id valid if not then it is to avoid a retry
                //and the person should be ignored
                persons.Add(person);
                updated = true;
              }
            }
            else
            {
              persons.Add(person);
            }
          }
        }

        foreach (PersonInfo person in persons)
        {
          //Try updating from cache
          if (!_wrapper.UpdateFromOnlineSeriesPerson(seriesMatch, person, language, true))
          {
            if (!forceQuickMode)
            {
              Logger.Debug(GetType().Name + ": Search for person {0} online", person.ToString());

              //Try to update movie information from online source if online Ids are present
              if (!_wrapper.UpdateFromOnlineSeriesPerson(seriesMatch, person, language, false))
              {
                //Search for the movie online and update the Ids if a match is found
                if (_wrapper.SearchPersonUniqueAndUpdate(person, language))
                {
                  //Ids were updated now try to fetch the online movie info
                  if (_wrapper.UpdateFromOnlineSeriesPerson(seriesMatch, person, language, false))
                  {
                    //Set as changed because cache has changed and might contain new/updated data
                    seriesInfo.HasChanged = true;
                    updated = true;
                  }
                }
              }
              else
              {
                updated = true;
              }
            }
          }
          else
          {
            Logger.Debug(GetType().Name + ": Found person {0} in cache", person.ToString());
            updated = true;
          }
        }

        if (updated)
        {
          //These lists contain Ids and other properties that are not loaded, so they will always appear changed.
          //So these changes will be ignored and only stored if there is any other reason for it to have changed.
          if (occupation == PersonAspect.OCCUPATION_ACTOR)
            MetadataUpdater.SetOrUpdateList(seriesInfo.Actors, seriesMatch.Actors, false);
        }

        List<string> thumbs = new List<string>();
        if (occupation == PersonAspect.OCCUPATION_ACTOR)
        {
          foreach (PersonInfo person in seriesInfo.Actors)
          {
            string id;
            if (GetPersonId(person, out id))
            {
              _actorMatcher.StoreNameMatch(id, person.Name, person.Name);
            }
            else
            {
              //Store empty match so he/she is not retried
              _actorMatcher.StoreNameMatch("", person.Name, person.Name);
            }
          }
        }

        return updated;
      }
      catch (Exception ex)
      {
        Logger.Debug(GetType().Name + ": Exception while processing persons {0}", ex, seriesInfo.ToString());
        return false;
      }
    }

    public virtual bool UpdateSeriesCharacters(SeriesInfo seriesInfo, bool forceQuickMode)
    {
      try
      {
        // Try online lookup
        if (!Init())
          return false;

        TLang language = FindBestMatchingLanguage(seriesInfo.Languages);
        bool updated = false;
        SeriesInfo seriesMatch = CloneProperties(seriesInfo);
        foreach (CharacterInfo character in seriesMatch.Characters)
        {
          string id;
          if (_characterMatcher.GetNameMatch(character.Name, out id))
          {
            if (SetCharacterId(character, id))
              updated = true;
            else
              continue;
          }

          //Try updating from cache
          if (!_wrapper.UpdateFromOnlineSeriesCharacter(seriesMatch, character, language, true))
          {
            if (!forceQuickMode)
            {
              Logger.Debug(GetType().Name + ": Search for character {0} online", character.ToString());

              //Try to update movie information from online source if online Ids are present
              if (!_wrapper.UpdateFromOnlineSeriesCharacter(seriesMatch, character, language, false))
              {
                //Search for the movie online and update the Ids if a match is found
                if (_wrapper.SearchCharacterUniqueAndUpdate(character, language))
                {
                  //Ids were updated now try to fetch the online movie info
                  if (_wrapper.UpdateFromOnlineSeriesCharacter(seriesMatch, character, language, false))
                  {
                    //Set as changed because cache has changed and might contain new/updated data
                    seriesInfo.HasChanged = true;
                    updated = true;
                  }
                }
              }
              else
              {
                updated = true;
              }
            }
          }
          else
          {
            Logger.Debug(GetType().Name + ": Found character {0} in cache", character.ToString());
            updated = true;
          }
        }

        if (updated)
        {
          //These lists contain Ids and other properties that are not loaded, so they will always appear changed.
          //So these changes will be ignored and only stored if there is any other reason for it to have changed.
          MetadataUpdater.SetOrUpdateList(seriesInfo.Characters, seriesMatch.Characters, false);
        }

        List<string> thumbs = new List<string>();
        foreach (CharacterInfo character in seriesInfo.Characters)
        {
          string id;
          if (GetCharacterId(character, out id))
          {
            _characterMatcher.StoreNameMatch(id, character.Name, character.Name);
          }
          else
          {
            //Store empty match so he/she is not retried
            _characterMatcher.StoreNameMatch("", character.Name, character.Name);
          }
        }

        return updated;
      }
      catch (Exception ex)
      {
        Logger.Debug(GetType().Name + ": Exception while processing characters {0}", ex, seriesInfo.ToString());
        return false;
      }
    }

    public virtual bool UpdateSeriesCompanies(SeriesInfo seriesInfo, string companyType, bool forceQuickMode)
    {
      try
      {
        // Try online lookup
        if (!Init())
          return false;

        TLang language = FindBestMatchingLanguage(seriesInfo.Languages);
        bool updated = false;
        SeriesInfo seriesMatch = CloneProperties(seriesInfo);
        List<CompanyInfo> companies = new List<CompanyInfo>();
        if (companyType == CompanyAspect.COMPANY_PRODUCTION)
        {
          foreach (CompanyInfo company in seriesMatch.ProductionCompanies)
          {
            string id;
            if (_companyMatcher.GetNameMatch(company.Name, out id))
            {
              if (SetCompanyId(company, id))
              {
                //Only add if Id valid if not then it is to avoid a retry
                //and the company should be ignored
                companies.Add(company);
                updated = true;
              }
            }
            else
            {
              companies.Add(company);
            }
          }
        }
        else if (companyType == CompanyAspect.COMPANY_TV_NETWORK)
        {
          foreach (CompanyInfo company in seriesMatch.Networks)
          {
            string id;
            if (_networkMatcher.GetNameMatch(company.Name, out id))
            {
              if (SetCompanyId(company, id))
              {
                //Only add if Id valid if not then it is to avoid a retry
                //and the company should be ignored
                companies.Add(company);
                updated = true;
              }
            }
            else
            {
              companies.Add(company);
            }
          }
        }
        foreach (CompanyInfo company in companies)
        {
          //Try updating from cache
          if (!_wrapper.UpdateFromOnlineSeriesCompany(seriesMatch, company, language, true))
          {
            if (!forceQuickMode)
            {
              Logger.Debug(GetType().Name + ": Search for company {0} online", company.ToString());

              //Try to update company information from online source if online Ids are present
              if (!_wrapper.UpdateFromOnlineSeriesCompany(seriesMatch, company, language, false))
              {
                //Search for the company online and update the Ids if a match is found
                if (_wrapper.SearchCompanyUniqueAndUpdate(company, language))
                {
                  //Ids were updated now try to fetch the online company info
                  if (_wrapper.UpdateFromOnlineSeriesCompany(seriesMatch, company, language, false))
                  {
                    //Set as changed because cache has changed and might contain new/updated data
                    seriesInfo.HasChanged = true;
                    updated = true;
                  }
                }
              }
              else
              {
                updated = true;
              }
            }
          }
          else
          {
            Logger.Debug(GetType().Name + ": Found company {0} in cache", company.ToString());
            updated = true;
          }
        }

        if (updated)
        {
          //These lists contain Ids and other properties that are not loaded, so they will always appear changed.
          //So these changes will be ignored and only stored if there is any other reason for it to have changed.
          if (companyType == CompanyAspect.COMPANY_PRODUCTION)
            MetadataUpdater.SetOrUpdateList(seriesInfo.ProductionCompanies, seriesMatch.ProductionCompanies, false);
          else if (companyType == CompanyAspect.COMPANY_TV_NETWORK)
            MetadataUpdater.SetOrUpdateList(seriesInfo.Networks, seriesMatch.Networks, false);
        }

        List<string> thumbs = new List<string>();
        if (companyType == CompanyAspect.COMPANY_PRODUCTION)
        {
          foreach (CompanyInfo company in seriesInfo.ProductionCompanies)
          {
            string id;
            if (GetCompanyId(company, out id))
            {
              _companyMatcher.StoreNameMatch(id, company.Name, company.Name);
            }
            else
            {
              //Store empty match so it is not retried
              _companyMatcher.StoreNameMatch("", company.Name, company.Name);
            }
          }
        }
        else if (companyType == CompanyAspect.COMPANY_TV_NETWORK)
        {
          foreach (CompanyInfo company in seriesInfo.Networks)
          {
            string id;
            if (GetCompanyId(company, out id))
            {
              _networkMatcher.StoreNameMatch(id, company.Name, company.Name);
            }
            else
            {
              //Store empty match so it is not retried
              _networkMatcher.StoreNameMatch("", company.Name, company.Name);
            }
          }
        }

        return updated;
      }
      catch (Exception ex)
      {
        Logger.Debug(GetType().Name + ": Exception while processing companies {0}", ex, seriesInfo.ToString());
        return false;
      }
    }

    public virtual bool UpdateEpisodePersons(EpisodeInfo episodeInfo, string occupation, bool forceQuickMode)
    {
      try
      {
        // Try online lookup
        if (!Init())
          return false;

        TLang language = FindBestMatchingLanguage(episodeInfo.Languages);
        bool updated = false;
        EpisodeInfo episodeMatch = CloneProperties(episodeInfo);
        List<PersonInfo> persons = new List<PersonInfo>();
        if (occupation == PersonAspect.OCCUPATION_ACTOR)
        {
          foreach (PersonInfo person in episodeMatch.Actors)
          {
            string id;
            if (_actorMatcher.GetNameMatch(person.Name, out id))
            {
              if (SetPersonId(person, id))
              {
                //Only add if Id valid if not then it is to avoid a retry
                //and the person should be ignored
                persons.Add(person);
                updated = true;
              }
            }
            else
            {
              persons.Add(person);
            }
          }
        }
        else if (occupation == PersonAspect.OCCUPATION_DIRECTOR)
        {
          foreach (PersonInfo person in episodeMatch.Directors)
          {
            string id;
            if (_directorMatcher.GetNameMatch(person.Name, out id))
            {
              if (SetPersonId(person, id))
              {
                //Only add if Id valid if not then it is to avoid a retry
                //and the person should be ignored
                persons.Add(person);
                updated = true;
              }
            }
            else
            {
              persons.Add(person);
            }
          }
        }
        else if (occupation == PersonAspect.OCCUPATION_WRITER)
        {
          foreach (PersonInfo person in episodeMatch.Writers)
          {
            string id;
            if (_writerMatcher.GetNameMatch(person.Name, out id))
            {
              if (SetPersonId(person, id))
              {
                //Only add if Id valid if not then it is to avoid a retry
                //and the person should be ignored
                persons.Add(person);
                updated = true;
              }
            }
            else
            {
              persons.Add(person);
            }
          }
        }
        foreach (PersonInfo person in persons)
        {
          //Try updating from cache
          if (!_wrapper.UpdateFromOnlineSeriesEpisodePerson(episodeMatch, person, language, true))
          {
            if (!forceQuickMode)
            {
              Logger.Debug(GetType().Name + ": Search for person {0} online", person.ToString());

              //Try to update person information from online source if online Ids are present
              if (!_wrapper.UpdateFromOnlineSeriesEpisodePerson(episodeMatch, person, language, false))
              {
                //Search for the person online and update the Ids if a match is found
                if (_wrapper.SearchPersonUniqueAndUpdate(person, language))
                {
                  //Ids were updated now try to fetch the online person info
                  if (_wrapper.UpdateFromOnlineSeriesEpisodePerson(episodeMatch, person, language, false))
                  {
                    //Set as changed because cache has changed and might contain new/updated data
                    episodeInfo.HasChanged = true;
                    updated = true;
                  }
                }
              }
              else
              {
                updated = true;
              }
            }
          }
          else
          {
            Logger.Debug(GetType().Name + ": Found person {0} in cache", person.ToString());
            updated = true;
          }
        }

        if (updated == false && occupation == PersonAspect.OCCUPATION_ACTOR)
        {
          //Try to update person based on series information
          SeriesInfo series = episodeMatch.CloneBasicInstance<SeriesInfo>();
          series.Actors = episodeMatch.Actors;
          if (UpdateSeriesPersons(series, occupation, forceQuickMode))
            updated = true;
        }

        if (updated)
        {
          //These lists contain Ids and other properties that are not loaded, so they will always appear changed.
          //So these changes will be ignored and only stored if there is any other reason for it to have changed.
          if (occupation == PersonAspect.OCCUPATION_ACTOR)
            MetadataUpdater.SetOrUpdateList(episodeInfo.Actors, episodeMatch.Actors, false);
          else if (occupation == PersonAspect.OCCUPATION_DIRECTOR)
            MetadataUpdater.SetOrUpdateList(episodeInfo.Directors, episodeMatch.Directors, false);
          else if (occupation == PersonAspect.OCCUPATION_WRITER)
            MetadataUpdater.SetOrUpdateList(episodeInfo.Writers, episodeMatch.Writers, false);
        }

        List<string> thumbs = new List<string>();
        if (occupation == PersonAspect.OCCUPATION_ACTOR)
        {
          foreach (PersonInfo person in episodeInfo.Actors)
          {
            string id;
            if (GetPersonId(person, out id))
            {
              _actorMatcher.StoreNameMatch(id, person.Name, person.Name);
            }
            else
            {
              //Store empty match so he/she is not retried
              _actorMatcher.StoreNameMatch("", person.Name, person.Name);
            }
          }
        }
        else if (occupation == PersonAspect.OCCUPATION_DIRECTOR)
        {
          foreach (PersonInfo person in episodeInfo.Directors)
          {
            string id;
            if (GetPersonId(person, out id))
            {
              _directorMatcher.StoreNameMatch(id, person.Name, person.Name);
            }
            else
            {
              //Store empty match so he/she is not retried
              _directorMatcher.StoreNameMatch("", person.Name, person.Name);
            }
          }
        }
        else if (occupation == PersonAspect.OCCUPATION_WRITER)
        {
          foreach (PersonInfo person in episodeInfo.Writers)
          {
            string id;
            if (GetPersonId(person, out id))
            {
              _writerMatcher.StoreNameMatch(id, person.Name, person.Name);
            }
            else
            {
              //Store empty match so he/she is not retried
              _writerMatcher.StoreNameMatch("", person.Name, person.Name);
            }
          }
        }

        return updated;
      }
      catch (Exception ex)
      {
        Logger.Debug(GetType().Name + ": Exception while processing persons {0}", ex, episodeInfo.ToString());
        return false;
      }
    }

    public virtual bool UpdateEpisodeCharacters(EpisodeInfo episodeInfo, bool forceQuickMode)
    {
      try
      {
        // Try online lookup
        if (!Init())
          return false;

        TLang language = FindBestMatchingLanguage(episodeInfo.Languages);
        bool updated = false;
        EpisodeInfo episodeMatch = CloneProperties(episodeInfo);
        foreach (CharacterInfo character in episodeMatch.Characters)
        {
          string id;
          if (_characterMatcher.GetNameMatch(character.Name, out id))
          {
            if (SetCharacterId(character, id))
              updated = true;
            else
              continue;
          }

          //Try updating from cache
          if (!_wrapper.UpdateFromOnlineSeriesEpisodeCharacter(episodeMatch, character, language, true))
          {
            if (!forceQuickMode)
            {
              Logger.Debug(GetType().Name + ": Search for character {0} online", character.ToString());

              //Try to update character information from online source if online Ids are present
              if (!_wrapper.UpdateFromOnlineSeriesEpisodeCharacter(episodeMatch, character, language, false))
              {
                //Search for the character online and update the Ids if a match is found
                if (_wrapper.SearchCharacterUniqueAndUpdate(character, language))
                {
                  //Ids were updated now try to fetch the online character info
                  if (_wrapper.UpdateFromOnlineSeriesEpisodeCharacter(episodeMatch, character, language, false))
                  {
                    //Set as changed because cache has changed and might contain new/updated data
                    episodeInfo.HasChanged = true;
                    updated = true;
                  }
                }
              }
              else
              {
                updated = true;
              }
            }
          }
          else
          {
            Logger.Debug(GetType().Name + ": Found character {0} in cache", character.ToString());
            updated = true;
          }
        }

        if (updated == false)
        {
          //Try to update character based on series information
          SeriesInfo series = episodeMatch.CloneBasicInstance<SeriesInfo>();
          series.Characters = episodeMatch.Characters;
          if (UpdateSeriesCharacters(series, forceQuickMode))
            updated = true;
        }

        if (updated)
        {
          //These lists contain Ids and other properties that are not loaded, so they will always appear changed.
          //So these changes will be ignored and only stored if there is any other reason for it to have changed.
          MetadataUpdater.SetOrUpdateList(episodeInfo.Characters, episodeMatch.Characters, false);
        }

        List<string> thumbs = new List<string>();
        foreach (CharacterInfo character in episodeInfo.Characters)
        {
          string id;
          if (GetCharacterId(character, out id))
          {
            _characterMatcher.StoreNameMatch(id, character.Name, character.Name);
          }
          else
          {
            //Store empty match so he/she is not retried
            _characterMatcher.StoreNameMatch("", character.Name, character.Name);
          }
        }

        return updated;
      }
      catch (Exception ex)
      {
        Logger.Debug(GetType().Name + ": Exception while processing characters {0}", ex, episodeInfo.ToString());
        return false;
      }
    }

    #endregion

    #region Metadata update helpers

    private T CloneProperties<T>(T obj)
    {
      if (obj == null)
        return default(T);
      Type type = obj.GetType();

      if (type.IsValueType || type == typeof(string))
      {
        return obj;
      }
      else if (type.IsArray)
      {
        Type elementType = obj.GetType().GetElementType();
        var array = obj as Array;
        Array arrayCopy = Array.CreateInstance(elementType, array.Length);
        for (int i = 0; i < array.Length; i++)
        {
          arrayCopy.SetValue(CloneProperties(array.GetValue(i)), i);
        }
        return (T)Convert.ChangeType(arrayCopy, obj.GetType());
      }
      else if (type.IsClass)
      {
        T newInstance = (T)Activator.CreateInstance(obj.GetType());
        FieldInfo[] fields = type.GetFields(BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.Instance);
        foreach (FieldInfo field in fields)
        {
          object fieldValue = field.GetValue(obj);
          if (fieldValue == null)
            continue;
          field.SetValue(newInstance, CloneProperties(fieldValue));
        }
        return newInstance;
      }
      return default(T);
    }

    private void StoreSeriesMatch(SeriesInfo seriesSearch, SeriesInfo seriesMatch)
    {
      if (seriesSearch.SeriesName.IsEmpty)
        return;

      string idValue = null;
      if (seriesMatch == null || !GetSeriesId(seriesSearch, out idValue) || seriesMatch.SeriesName.IsEmpty)
      {
        _storage.TryAddMatch(new SeriesMatch()
        {
          ItemName = seriesSearch.SeriesName.ToString()
        });
        return;
      }

      var onlineMatch = new SeriesMatch
      {
        Id = idValue,
        ItemName = seriesSearch.SeriesName.ToString(),
        OnlineName = seriesMatch.SeriesName.ToString(),
        Year = seriesSearch.FirstAired.HasValue ? seriesSearch.FirstAired.Value.Year :
            seriesMatch.FirstAired.HasValue ? seriesMatch.FirstAired.Value.Year : 0
      };
      _storage.TryAddMatch(onlineMatch);
    }

    protected virtual TLang FindBestMatchingLanguage(List<string> mediaLanguages)
    {
      if (typeof(TLang) == typeof(string))
      {
        CultureInfo mpLocal = ServiceRegistration.Get<ILocalization>().CurrentCulture;
        // If we don't have movie languages available, or the MP2 setting language is available, prefer it.
        if (mediaLanguages.Count == 0 || mediaLanguages.Contains(mpLocal.TwoLetterISOLanguageName))
          return (TLang)Convert.ChangeType(mpLocal.TwoLetterISOLanguageName, typeof(TLang));

        // If there is only one language available, use this one.
        if (mediaLanguages.Count == 1)
          return (TLang)Convert.ChangeType(mediaLanguages[0], typeof(TLang));
      }
      // If there are multiple languages, that are different to MP2 setting, we cannot guess which one is the "best".
      // By returning null we allow fallback to the default language of the online source (en).
      return default(TLang);
    }

    protected virtual TLang FindMatchingLanguage(string shortLanguageString)
    {
      if (typeof(TLang) == typeof(string) && !string.IsNullOrEmpty(shortLanguageString))
      {
        return (TLang)Convert.ChangeType(shortLanguageString, typeof(TLang));
      }
      return default(TLang);
    }

    #endregion

    #region Ids

    protected abstract bool GetSeriesId(SeriesInfo series, out string id);

    protected abstract bool SetSeriesId(SeriesInfo series, string id);

    protected abstract bool SetSeriesId(EpisodeInfo episode, string id);

    protected virtual bool GetSeriesSeasonId(SeasonInfo season, out string id)
    {
      id = null;
      return false;
    }

    protected virtual bool SetSeriesSeasonId(SeasonInfo season, string id)
    {
      return false;
    }

    protected virtual bool GetSeriesEpisodeId(EpisodeInfo episode, out string id)
    {
      id = null;
      return false;
    }

    protected virtual bool SetSeriesEpisodeId(EpisodeInfo episode, string id)
    {
      return false;
    }

    protected virtual bool GetPersonId(PersonInfo person, out string id)
    {
      id = null;
      return false;
    }

    protected virtual bool SetPersonId(PersonInfo person, string id)
    {
      return false;
    }

    protected virtual bool GetCharacterId(CharacterInfo character, out string id)
    {
      id = null;
      return false;
    }

    protected virtual bool SetCharacterId(CharacterInfo character, string id)
    {
      return false;
    }

    protected virtual bool GetCompanyId(CompanyInfo company, out string id)
    {
      id = null;
      return false;
    }

    protected virtual bool SetCompanyId(CompanyInfo company, string id)
    {
      return false;
    }
    #endregion

    #region Caching

    /// <summary>
    /// Check if the memory cache should be cleared and starts an online update of (file-) cached series information.
    /// </summary>
    private void CheckCacheAndRefresh()
    {
      if (DateTime.Now - _memoryCacheInvalidated <= _maxCacheDuration)
        return;
      _memoryCache.Clear();
      _memoryCacheEpisode.Clear();
      _memoryCacheInvalidated = DateTime.Now;

      RefreshCache();
    }

    protected virtual void RefreshCache()
    {
      string dateFormat = "MMddyyyyHHmm";
      if (string.IsNullOrEmpty(_config.LastRefresh))
        _config.LastRefresh = DateTime.Now.ToString(dateFormat);

      DateTime lastRefresh = DateTime.ParseExact(_config.LastRefresh, dateFormat, CultureInfo.InvariantCulture);

      if (DateTime.Now - lastRefresh <= _maxCacheDuration)
        return;

      IThreadPool threadPool = ServiceRegistration.Get<IThreadPool>(false);
      if (threadPool != null)
      {
        Logger.Debug(GetType().Name + ": Refreshing local cache");
        threadPool.Add(() =>
        {
          if (_wrapper != null)
            _wrapper.RefreshCache(lastRefresh);
        });
      }

      _config.LastRefresh = DateTime.Now.ToString(dateFormat, CultureInfo.InvariantCulture);
      SaveConfig();
    }

    #endregion

    #region FanArt

    public virtual bool ScheduleFanArtDownload(Guid mediaItemId, BaseInfo info)
    {
      string id;
      info.InitFanArtToken();
      FanArtCache.InitFanArtCache(mediaItemId.ToString().ToUpperInvariant(), info.ToString());
      if (info is SeriesInfo)
      {
        SeriesInfo seriesInfo = info as SeriesInfo;
        if (GetSeriesId(seriesInfo, out id))
        {
          TLang language = FindBestMatchingLanguage(seriesInfo.Languages);
          DownloadData data = new DownloadData()
          {
            FanArtToken = seriesInfo.FanArtToken,
            FanArtMediaType = FanArtMediaTypes.Series,
            ShortLanguage = language != null ? language.ToString() : "",
            MediaItemId = mediaItemId.ToString().ToUpperInvariant(),
            Name = seriesInfo.ToString()
          };
          data.FanArtId[FanArtMediaTypes.Series] = id;
          return ScheduleDownload(data.Serialize());
        }
      }
      else if (info is SeasonInfo)
      {
        SeasonInfo seasonInfo = info as SeasonInfo;
        if (seasonInfo != null)
        {
          TLang language = FindBestMatchingLanguage(seasonInfo.Languages);
          DownloadData data = new DownloadData()
          {
            FanArtToken = seasonInfo.FanArtToken,
            FanArtMediaType = FanArtMediaTypes.SeriesSeason,
            ShortLanguage = language != null ? language.ToString() : "",
            MediaItemId = mediaItemId.ToString().ToUpperInvariant(),
            Name = seasonInfo.ToString()
          };
          if (GetSeriesId(seasonInfo.CloneBasicInstance<SeriesInfo>(), out id))
          {
            data.FanArtId[FanArtMediaTypes.Series] = id;
          }
          if (seasonInfo.SeasonNumber.HasValue)
          {
            data.FanArtId[FanArtMediaTypes.SeriesSeason] = seasonInfo.SeasonNumber.Value.ToString();
          }
          if (GetSeriesSeasonId(seasonInfo, out id))
          {
            data.FanArtId[FanArtMediaTypes.Undefined] = id;
          }
          ScheduleDownload(data.Serialize());
        }
      }
      else if (info is EpisodeInfo)
      {
        EpisodeInfo episodeInfo = info as EpisodeInfo;
        if (episodeInfo != null)
        {
          TLang language = FindBestMatchingLanguage(episodeInfo.Languages);
          DownloadData data = new DownloadData()
          {
            FanArtToken = episodeInfo.FanArtToken,
            FanArtMediaType = FanArtMediaTypes.Episode,
            ShortLanguage = language != null ? language.ToString() : "",
            MediaItemId = mediaItemId.ToString().ToUpperInvariant(),
            Name = episodeInfo.ToString()
          };
          if (GetSeriesId(episodeInfo.CloneBasicInstance<SeriesInfo>(), out id))
          {
            data.FanArtId[FanArtMediaTypes.Series] = id;
          }
          if (episodeInfo.SeasonNumber.HasValue)
          {
            data.FanArtId[FanArtMediaTypes.SeriesSeason] = episodeInfo.SeasonNumber.Value.ToString();
          }
          if (episodeInfo.EpisodeNumbers.Count > 0)
          {
            data.FanArtId[FanArtMediaTypes.Episode] = episodeInfo.EpisodeNumbers[0].ToString();
          }
          if (GetSeriesEpisodeId(episodeInfo, out id))
          {
            data.FanArtId[FanArtMediaTypes.Undefined] = id;
          }
          ScheduleDownload(data.Serialize());
        }
      }
      else if (info is CompanyInfo)
      {
        CompanyInfo companyInfo = info as CompanyInfo;
        if (GetCompanyId(companyInfo, out id))
        {
          DownloadData data = new DownloadData()
          {
            FanArtToken = companyInfo.FanArtToken,
            ShortLanguage = "",
            MediaItemId = mediaItemId.ToString().ToUpperInvariant(),
            Name = companyInfo.ToString()
          };
          if (companyInfo.Type == CompanyAspect.COMPANY_PRODUCTION)
          {
            data.FanArtMediaType = FanArtMediaTypes.Company;
            data.FanArtId[FanArtMediaTypes.Company] = id;
          }
          else if (companyInfo.Type == CompanyAspect.COMPANY_TV_NETWORK)
          {
            data.FanArtMediaType = FanArtMediaTypes.TVNetwork;
            data.FanArtId[FanArtMediaTypes.TVNetwork] = id;
          }
          return ScheduleDownload(data.Serialize());
        }
      }
      else if (info is CharacterInfo)
      {
        CharacterInfo characterInfo = info as CharacterInfo;
        if (GetCharacterId(characterInfo, out id))
        {
          DownloadData data = new DownloadData()
          {
            FanArtToken = characterInfo.FanArtToken,
            FanArtMediaType = FanArtMediaTypes.Character,
            ShortLanguage = "",
            MediaItemId = mediaItemId.ToString().ToUpperInvariant(),
            Name = characterInfo.ToString()
          };
          data.FanArtId[FanArtMediaTypes.Character] = id;

          string actorId;
          PersonInfo actor = characterInfo.CloneBasicInstance<PersonInfo>();
          if (GetPersonId(actor, out actorId))
          {
            data.FanArtId[FanArtMediaTypes.Actor] = actorId;
          }
          return ScheduleDownload(data.Serialize());
        }
      }
      else if (info is PersonInfo)
      {
        PersonInfo personInfo = info as PersonInfo;
        if (GetPersonId(personInfo, out id))
        {
          DownloadData data = new DownloadData()
          {
            FanArtToken = personInfo.FanArtToken,
            ShortLanguage = "",
            MediaItemId = mediaItemId.ToString().ToUpperInvariant(),
            Name = personInfo.ToString()
          };
          if (personInfo.Occupation == PersonAspect.OCCUPATION_ACTOR)
          {
            data.FanArtMediaType = FanArtMediaTypes.Actor;
            data.FanArtId[FanArtMediaTypes.Actor] = id;
          }
          else if (personInfo.Occupation == PersonAspect.OCCUPATION_DIRECTOR)
          {
            data.FanArtMediaType = FanArtMediaTypes.Director;
            data.FanArtId[FanArtMediaTypes.Director] = id;
          }
          else if (personInfo.Occupation == PersonAspect.OCCUPATION_WRITER)
          {
            data.FanArtMediaType = FanArtMediaTypes.Writer;
            data.FanArtId[FanArtMediaTypes.Writer] = id;
          }
          return ScheduleDownload(data.Serialize());
        }
      }
      return false;
    }

    protected override void DownloadFanArt(string downloadId)
    {
      string name = downloadId;
      try
      {
        if (string.IsNullOrEmpty(downloadId))
          return;

        DownloadData data = new DownloadData();
        if (!data.Deserialize(downloadId))
          return;

        name = string.Format("{0} ({1})", data.MediaItemId, data.Name);

        if (!Init())
          return;

        string[] fanArtTypes = new string[]
        {
          FanArtTypes.FanArt,
          FanArtTypes.Poster,
          FanArtTypes.Banner,
          FanArtTypes.ClearArt,
          FanArtTypes.Cover,
          FanArtTypes.DiscArt,
          FanArtTypes.Logo,
          FanArtTypes.Thumbnail
        };

        try
        {
          string seriesId = null;
          string seasonId = null;
          string episodeId = null;
          string seasonNo = null;
          string episodeNo = null;
          TLang language = FindMatchingLanguage(data.ShortLanguage);
          foreach (string fanArtType in fanArtTypes)
            InitFanArtCount(data.MediaItemId, data.FanArtToken, fanArtType);

          Logger.Debug(GetType().Name + " Download: Started for media item {0}", name);
          ApiWrapperImageCollection<TImg> images = null;
          string Id = "";
          if (data.FanArtMediaType == FanArtMediaTypes.Series)
          {
            Id = data.FanArtId[FanArtMediaTypes.Series];
            seriesId = Id;
            SeriesInfo seriesInfo = new SeriesInfo();
            if (SetSeriesId(seriesInfo, seriesId))
            {
              if (_wrapper.GetFanArt(seriesInfo, language, data.FanArtMediaType, out images) == false)
              {
                Logger.Debug(GetType().Name + " Download: Failed getting images for series ID {0} [{1}]", Id, name);
                return;
              }

              //Not used
              images.Thumbnails.Clear();
            }
          }
          else if (data.FanArtMediaType == FanArtMediaTypes.SeriesSeason)
          {
            if (data.FanArtId.ContainsKey(FanArtMediaTypes.Undefined))
            {
              seasonId = data.FanArtId[FanArtMediaTypes.Undefined];
            }
            if (data.FanArtId.ContainsKey(FanArtMediaTypes.Series))
            {
              seriesId = data.FanArtId[FanArtMediaTypes.Series];
            }
            if (data.FanArtId.ContainsKey(FanArtMediaTypes.SeriesSeason))
            {
              seasonNo = data.FanArtId[FanArtMediaTypes.SeriesSeason];
            }
            if (data.FanArtId.ContainsKey(FanArtMediaTypes.Episode))
            {
              episodeNo = data.FanArtId[FanArtMediaTypes.Episode];
            }
            SeriesInfo seriesInfo = new SeriesInfo();
            SeasonInfo seasonInfo = new SeasonInfo();
            if (SetSeriesId(seriesInfo, seriesId))
            {
              seasonInfo.CopyIdsFrom(seriesInfo);
            }
            SetSeriesSeasonId(seasonInfo, seasonId);
            if (seasonNo != null)
            {
              seasonInfo.SeasonNumber = Convert.ToInt32(seasonNo);
            }
            if (_wrapper.GetFanArt(seasonInfo, language, data.FanArtMediaType, out images) == false)
            {
              Logger.Debug(GetType().Name + " Download: Failed getting images for series season {0} [{1}]", Id, name);
              return;
            }

            //Not used
            images.Thumbnails.Clear();
          }
          else if (data.FanArtMediaType == FanArtMediaTypes.Episode)
          {
            if (data.FanArtId.ContainsKey(FanArtMediaTypes.Undefined))
            {
              episodeId = data.FanArtId[FanArtMediaTypes.Undefined];
            }
            if (data.FanArtId.ContainsKey(FanArtMediaTypes.Series))
            {
              seriesId = data.FanArtId[FanArtMediaTypes.Series];
            }
            if (data.FanArtId.ContainsKey(FanArtMediaTypes.SeriesSeason))
            {
              seasonNo = data.FanArtId[FanArtMediaTypes.SeriesSeason];
            }
            if (data.FanArtId.ContainsKey(FanArtMediaTypes.Episode))
            {
              episodeNo = data.FanArtId[FanArtMediaTypes.Episode];
            }
            SeriesInfo seriesInfo = new SeriesInfo();
            EpisodeInfo episodeInfo = new EpisodeInfo();
            if (SetSeriesId(seriesInfo, seriesId))
            {
              episodeInfo.CopyIdsFrom(seriesInfo);
            }
            SetSeriesEpisodeId(episodeInfo, episodeId);
            if (seasonNo != null)
            {
              episodeInfo.SeasonNumber = Convert.ToInt32(seasonNo);
            }
            if (episodeNo != null)
            {
              episodeInfo.EpisodeNumbers.Add(Convert.ToInt32(episodeNo));
            }
            if (_wrapper.GetFanArt(episodeInfo, language, data.FanArtMediaType, out images) == false)
            {
              Logger.Debug(GetType().Name + " Download: Failed getting images for series episode {0} [{1}]", Id, name);
              return;
            }
          }
          else if (data.FanArtMediaType == FanArtMediaTypes.Actor || data.FanArtMediaType == FanArtMediaTypes.Director || data.FanArtMediaType == FanArtMediaTypes.Writer)
          {
            if (OnlyBasicFanArt)
              return;

            Id = data.FanArtId[data.FanArtMediaType];
            PersonInfo personInfo = new PersonInfo();
            if (SetPersonId(personInfo, Id))
            {
              if (_wrapper.GetFanArt(personInfo, language, data.FanArtMediaType, out images) == false)
              {
                Logger.Debug(GetType().Name + " Download: Failed getting images for series person ID {0} [{1}]", Id, name);
                return;
              }
            }
          }
          else if (data.FanArtMediaType == FanArtMediaTypes.Character)
          {
            if (OnlyBasicFanArt)
              return;

            Id = data.FanArtId[FanArtMediaTypes.Character];
            CharacterInfo characterInfo = new CharacterInfo();
            if (SetCharacterId(characterInfo, Id))
            {
              if (_wrapper.GetFanArt(characterInfo, language, data.FanArtMediaType, out images) == false)
              {
                Logger.Debug(GetType().Name + " Download: Failed getting images for series character ID {0} [{1}]", Id, name);
                return;
              }
            }
          }
          else if (data.FanArtMediaType == FanArtMediaTypes.Company || data.FanArtMediaType == FanArtMediaTypes.TVNetwork)
          {
            if (OnlyBasicFanArt)
              return;

            Id = data.FanArtId[data.FanArtMediaType];
            CompanyInfo companyInfo = new CompanyInfo();
            if (SetCompanyId(companyInfo, Id))
            {
              if (_wrapper.GetFanArt(companyInfo, language, data.FanArtMediaType, out images) == false)
              {
                Logger.Debug(GetType().Name + " Download: Failed getting images for series company ID {0} [{1}]", Id, name);
                return;
              }
            }
          }

          if (images != null)
          {
            Logger.Debug(GetType().Name + " Download: Downloading images for ID {0} [{1}]", Id, name);

            SaveFanArtImages(data.FanArtToken, images.Id, images.Backdrops, data.MediaItemId, data.Name, FanArtTypes.FanArt);
            SaveFanArtImages(data.FanArtToken, images.Id, images.Posters, data.MediaItemId, data.Name, FanArtTypes.Poster);
            SaveFanArtImages(data.FanArtToken, images.Id, images.Banners, data.MediaItemId, data.Name, FanArtTypes.Banner);
            SaveFanArtImages(data.FanArtToken, images.Id, images.Covers, data.MediaItemId, data.Name, FanArtTypes.Cover);
            SaveFanArtImages(data.FanArtToken, images.Id, images.Thumbnails, data.MediaItemId, data.Name, FanArtTypes.Thumbnail);

            if (!OnlyBasicFanArt)
            {
              SaveFanArtImages(data.FanArtToken, images.Id, images.ClearArt, data.MediaItemId, data.Name, FanArtTypes.ClearArt);
              SaveFanArtImages(data.FanArtToken, images.Id, images.DiscArt, data.MediaItemId, data.Name, FanArtTypes.DiscArt);
              SaveFanArtImages(data.FanArtToken, images.Id, images.Logos, data.MediaItemId, data.Name, FanArtTypes.Logo);
            }

            Logger.Debug(GetType().Name + " Download: Finished saving images for ID {0} [{1}]", Id, name);
          }
        }
        finally
        {
          // Remember we are finished
          FinishDownloadFanArt(downloadId);
        }
      }
      catch (Exception ex)
      {
        Logger.Debug(GetType().Name + " Download: Exception downloading images for {0}", ex, name);
      }
    }

    protected virtual bool VerifyFanArtImage(TImg image)
    {
      return image != null;
    }

    protected virtual int SaveFanArtImages(string fanArtToken, string id, IEnumerable<TImg> images, string mediaItemId, string name, string fanartType)
    {
      try
      {
        if (images == null)
          return 0;

        int idx = 0;
        foreach (TImg img in images)
        {
          using (FanArtCountLock countLock = GetFanArtCountLock(fanArtToken, fanartType))
          {
            if (countLock.Count >= FanArtCache.MAX_FANART_IMAGES[fanartType])
              break;
            if (!VerifyFanArtImage(img))
              continue;
            if (idx >= FanArtCache.MAX_FANART_IMAGES[fanartType])
              break;
            if (_wrapper.DownloadFanArt(id, img, Path.Combine(FANART_CACHE_PATH, mediaItemId, fanartType)))
            {
              countLock.Count++;
              idx++;
            }
            else
            {
              Logger.Warn(GetType().Name + " Download: Error downloading FanArt for ID {0} on media item {1} ({2}) of type {3}", id, mediaItemId, name, fanartType);
            }
          }
        }
        Logger.Debug(GetType().Name + @" Download: Saved {0} for media item {1} ({2}) of type {3}", idx, mediaItemId, name, fanartType);
        return idx;
      }
      catch (Exception ex)
      {
        Logger.Debug(GetType().Name + " Download: Exception downloading images for ID {0} [{1} ({2})]", ex, id, mediaItemId, name);
        return 0;
      }
    }

    #endregion
  }
}