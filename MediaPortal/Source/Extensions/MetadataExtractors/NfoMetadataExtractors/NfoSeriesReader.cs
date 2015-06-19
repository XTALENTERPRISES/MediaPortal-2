﻿#region Copyright (C) 2007-2015 Team MediaPortal

/*
    Copyright (C) 2007-2014 Team MediaPortal
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
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;
using MediaPortal.Common.Logging;
using MediaPortal.Common.ResourceAccess;
using MediaPortal.Extensions.MetadataExtractors.NfoMetadataExtractors.Stubs;

namespace MediaPortal.Extensions.MetadataExtractors.NfoMetadataExtractors
{
  /// <summary>
  /// Reads the content of a nfo-file for series (tvshow.nfo) into <see cref="SeriesStub"/> objects. It does not
  /// write anything into the MediaItemsAspects because the SeriesStub objects need to be combined
  /// with the SeriesEpisodeStub object before writing to the MediaItemAspects, which is done in the
  /// NfoSeriesEpisodeReader.
  /// </summary>
  /// <remarks>
  /// There is a TryRead method for any known child element of the nfo-file's root element.
  /// There are no TryWrite mothods in this class; the <see cref="SeriesStub"/> objects generated by this class
  /// need to be taken (via <see cref="GetSeriesStubs"/>) and put into a <see cref="NfoSeriesEpisodeReader"/> object,
  /// which holds the appropriate TryWriteMethods to write information from both, <see cref="SeriesStub"/> and
  /// <see cref="SeriesEpisodeStub"/> objects into the appropriate MIA-Attributes.
  /// </remarks>
  class NfoSeriesReader : NfoReaderBase<SeriesStub>
  {
    #region Consts / static fields

    /// <summary>
    /// The name of the root element in a valid nfo-file for series
    /// </summary>
    private const string SERIES_ROOT_ELEMENT_NAME = "tvshow";

    /// <summary>
    /// Default timeout for the cache is 5 minutes
    /// </summary>
    private static readonly TimeSpan CACHE_TIMEOUT = new TimeSpan(0,5,0);

    /// <summary>
    /// Cache used to temporarily store <see cref="SeriesStub"/> objects so that the same tvshow.nfo file
    /// doesn't have to be parsed once for every episode
    /// </summary>
    private static readonly AsyncStaticTimeoutCache<ResourcePath, List<SeriesStub>> CACHE = new AsyncStaticTimeoutCache<ResourcePath, List<SeriesStub>>(CACHE_TIMEOUT);

    #endregion

    #region Private fields

    private readonly NfoSeriesMetadataExtractorSettings _settings;

    #endregion

    #region Ctor

    /// <summary>
    /// Instantiates a <see cref="NfoSeriesReader"/> object
    /// </summary>
    /// <param name="debugLogger">Debug logger to log to</param>
    /// <param name="miNumber">Unique number of the MediaItem for which the nfo-file is parsed</param>
    /// <param name="forceQuickMode">If true, no long lasting operations such as parsing images are performed</param>
    /// <param name="httpClient"><see cref="HttpClient"/> used to download from http URLs contained in nfo-files</param>
    /// <param name="settings">Settings of the <see cref="NfoSeriesMetadataExtractor"/></param>
    public NfoSeriesReader(ILogger debugLogger, long miNumber, bool forceQuickMode, HttpClient httpClient, NfoSeriesMetadataExtractorSettings settings)
      : base(debugLogger, miNumber, forceQuickMode, httpClient, settings)
    {
      _settings = settings;
      InitializeSupportedElements();
    }

    #endregion

    #region Private methods

    #region Ctor helpers

    /// <summary>
    /// Adds a delegate for each xml element in a series nfo-file that is understood by this MetadataExtractor to NfoReaderBase.SupportedElements
    /// </summary>
    private void InitializeSupportedElements()
    {
      SupportedElements.Add("id", new TryReadElementDelegate(TryReadId));
      SupportedElements.Add("code", new TryReadElementDelegate(TryReadCode));
      SupportedElements.Add("episodeguide", new TryReadElementDelegate(TryReadEpisodeGuide));

      SupportedElements.Add("title", new TryReadElementDelegate(TryReadTitle));
      SupportedElements.Add("showtitle", new TryReadElementDelegate(TryReadShowTitle));
      SupportedElements.Add("sorttitle", new TryReadElementDelegate(TryReadSortTitle));
      SupportedElements.Add("set", new TryReadElementAsyncDelegate(TryReadSetAsync));
      SupportedElements.Add("sets", new TryReadElementAsyncDelegate(TryReadSetsAsync));
      
      SupportedElements.Add("premiered", new TryReadElementDelegate(TryReadPremiered));
      SupportedElements.Add("year", new TryReadElementDelegate(TryReadYear));
      SupportedElements.Add("studio", new TryReadElementDelegate(TryReadStudio));
      SupportedElements.Add("actor", new TryReadElementAsyncDelegate(TryReadActorAsync));
      SupportedElements.Add("status", new TryReadElementDelegate(TryReadStatus));

      SupportedElements.Add("plot", new TryReadElementDelegate(TryReadPlot));
      SupportedElements.Add("outline", new TryReadElementDelegate(TryReadOutline));
      SupportedElements.Add("tagline", new TryReadElementDelegate(TryReadTagline));
      SupportedElements.Add("trailer", new TryReadElementDelegate(TryReadTrailer));
      SupportedElements.Add("genre", new TryReadElementDelegate(TryReadGenre));
      SupportedElements.Add("genres", new TryReadElementDelegate(TryReadGenres));

      SupportedElements.Add("thumb", new TryReadElementAsyncDelegate(TryReadThumbAsync));
      SupportedElements.Add("fanart", new TryReadElementAsyncDelegate(TryReadFanArtAsync));

      SupportedElements.Add("mpaa", new TryReadElementDelegate(TryReadMpaa));
      SupportedElements.Add("rating", new TryReadElementDelegate(TryReadRating));
      SupportedElements.Add("votes", new TryReadElementDelegate(TryReadVotes));
      SupportedElements.Add("top250", new TryReadElementDelegate(TryReadTop250));

      // The following elements are contained in many tvshow.nfo files, but have no meaning
      // in the context of a series. We add them here to avoid them being logged as
      // unknown elements, but we simply ignore them.
      // For reference see here: http://forum.team-mediaportal.com/threads/mp2-459-implementation-of-a-movienfometadataextractor-and-a-seriesnfometadataextractor.128805/page-13#post-1130414
      SupportedElements.Add("epbookmark", new TryReadElementDelegate(Ignore));
      SupportedElements.Add("season", new TryReadElementDelegate(Ignore));
      SupportedElements.Add("episode", new TryReadElementDelegate(Ignore)); // Kodi stores a value here, but the meaning is unclear
      SupportedElements.Add("displayseason", new TryReadElementDelegate(Ignore));
      SupportedElements.Add("displayepisode", new TryReadElementDelegate(Ignore));
      SupportedElements.Add("uniqueid", new TryReadElementDelegate(Ignore));
      SupportedElements.Add("aired", new TryReadElementDelegate(Ignore));
      SupportedElements.Add("runtime", new TryReadElementDelegate(Ignore));
      SupportedElements.Add("playcount", new TryReadElementDelegate(Ignore));
      SupportedElements.Add("lastplayed", new TryReadElementDelegate(Ignore));
      SupportedElements.Add("resume", new TryReadElementDelegate(Ignore));
      SupportedElements.Add("dateadded", new TryReadElementDelegate(Ignore));
    }

    #endregion

    #region Reader methods for direct child elements of the root element

    #region Internet databases

    /// <summary>
    /// Tries to read the thetvdb.com ID
    /// </summary>
    /// <param name="element"><see cref="XElement"/> to read from</param>
    /// <returns><c>true</c> if a value was found in <paramref name="element"/>; otherwise <c>false</c></returns>
    private bool TryReadId(XElement element)
    {
      // Example of a valid element:
      // <id>158661</id>
      return ((CurrentStub.Id = ParseSimpleInt(element)) != null);
    }

    /// <summary>
    /// Tries to read the Production Code Number
    /// </summary>
    /// <param name="element"><see cref="XElement"/> to read from</param>
    /// <returns><c>true</c> if a value was found in <paramref name="element"/>; otherwise <c>false</c></returns>
    private bool TryReadCode(XElement element)
    {
      // Example of a valid element:
      // <code>A</code>
      return ((CurrentStub.ProductionCodeNumber = ParseSimpleString(element)) != null);
    }

    /// <summary>
    /// Tries to read the Link to the Episode Guide
    /// </summary>
    /// <param name="element"><see cref="XElement"/> to read from</param>
    /// <returns><c>true</c> if a value was found in <paramref name="element"/>; otherwise <c>false</c></returns>
    private bool TryReadEpisodeGuide(XElement element)
    {
      // Example of a valid element:
      // <episodeguide>
      //   <url cache="83462-de.xml">http://thetvdb.com/api/1D62F2F90030C444/series/83462/all/de.zip</url>
      // </episodeguide>
      if (element == null || !element.HasElements)
        return false;
      if (element.Elements().Count() > 1)
        DebugLogger.Warn("[#{0}]: EpisodeGuide element has multiple child elements. Only using the first url child element {1}", MiNumber, element);
      var urlElements = element.Elements("url").ToList();
      if (!urlElements.Any())
      {
        DebugLogger.Warn("[#{0}]: EpisodeGuide element missing url child element {1}", MiNumber, element);
        return false;
      }
      return ((CurrentStub.EpisodeGuide = ParseSimpleString(urlElements[0])) != null);
    }

    #endregion

    #region Title information

    /// <summary>
    /// Tries to read the title
    /// </summary>
    /// <param name="element"><see cref="XElement"/> to read from</param>
    /// <returns><c>true</c> if a value was found in <paramref name="element"/>; otherwise <c>false</c></returns>
    private bool TryReadTitle(XElement element)
    {
      // Example of a valid element:
      // <title>Castle</title>
      return ((CurrentStub.Title = ParseSimpleString(element)) != null);
    }

    /// <summary>
    /// Tries to read the show title
    /// </summary>
    /// <param name="element"><see cref="XElement"/> to read from</param>
    /// <returns><c>true</c> if a value was found in <paramref name="element"/>; otherwise <c>false</c></returns>
    private bool TryReadShowTitle(XElement element)
    {
      // Example of a valid element:
      // <showtitle>Castle</showtitle>
      return ((CurrentStub.ShowTitle = ParseSimpleString(element)) != null);
    }

    /// <summary>
    /// Tries to read the sort title
    /// </summary>
    /// <param name="element"><see cref="XElement"/> to read from</param>
    /// <returns><c>true</c> if a value was found in <paramref name="element"/>; otherwise <c>false</c></returns>
    private bool TryReadSortTitle(XElement element)
    {
      // Example of a valid element:
      // <sorttitle>Star Trek02</sorttitle>
      return ((CurrentStub.SortTitle = ParseSimpleString(element)) != null);
    }

    /// <summary>
    /// Tries to (asynchronously) read the set information
    /// We have not found an example for this element, yet, and assume it has the same structure as for movies.
    /// </summary>
    /// <param name="element"><see cref="XElement"/> to read from</param>
    /// <param name="nfoDirectoryFsra"><see cref="IFileSystemResourceAccessor"/> to the parent directory of the nfo-file</param>
    /// <returns><c>true</c> if a value was found in <paramref name="element"/>; otherwise <c>false</c></returns>
    private async Task<bool> TryReadSetAsync(XElement element, IFileSystemResourceAccessor nfoDirectoryFsra)
    {
      // Examples of valid elements:
      // 1:
      // <set order = "1">Star Trek</set>
      // 2:
      // <set order = "1">
      //   <setname>Star Trek</setname>
      //   <setdescription>This is ...</setdescription>
      //   <setrule></setrule>
      //   <setimage></setimage>
      // </set>
      // The order attribute in both cases is optional.
      // In example 2 only the <setname> child element is mandatory.
      if (element == null)
        return false;

      var value = new SetStub();
      // Example 1:
      if (!element.HasElements)
        value.Name = ParseSimpleString(element);
      // Example 2:
      else
      {
        value.Name = ParseSimpleString(element.Element("setname"));
        value.Description = ParseSimpleString(element.Element("setdescription"));
        value.Rule = ParseSimpleString(element.Element("setrule"));
        value.Image = await ParseSimpleImageAsync(element.Element("setimage"), nfoDirectoryFsra);
      }
      value.Order = ParseIntAttribute(element, "order");

      if (value.Name == null)
        return false;

      if (CurrentStub.Sets == null)
        CurrentStub.Sets = new HashSet<SetStub>();
      CurrentStub.Sets.Add(value);
      return true;
    }

    /// <summary>
    /// Tries to (asynchronously) read the sets information
    /// We have not found an example for this element, yet, and assume it has the same structure as for movies.
    /// </summary>
    /// <param name="element"><see cref="XElement"/> to read from</param>
    /// <param name="nfoDirectoryFsra"><see cref="IFileSystemResourceAccessor"/> to the parent directory of the nfo-file</param>
    /// <returns><c>true</c> if a value was found in <paramref name="element"/>; otherwise <c>false</c></returns>
    private async Task<bool> TryReadSetsAsync(XElement element, IFileSystemResourceAccessor nfoDirectoryFsra)
    {
      // Example of a valid element:
      // <sets>
      //   [any number of set elements that can be read by TryReadSetAsync]
      // </sets>
      if (element == null || !element.HasElements)
        return false;
      var result = false;
      foreach (var childElement in element.Elements())
        if (childElement.Name == "set")
        {
          if (await TryReadSetAsync(childElement, nfoDirectoryFsra))
            result = true;
        }
        else
          DebugLogger.Warn("[#{0}]: Unknown child element: {1}", MiNumber, childElement);
      return result;
    }

    #endregion

    #region Making-of information

    /// <summary>
    /// Tries to read the premiered value
    /// </summary>
    /// <param name="element"><see cref="XElement"/> to read from</param>
    /// <returns><c>true</c> if a value was found in <paramref name="element"/>; otherwise <c>false</c></returns>
    private bool TryReadPremiered(XElement element)
    {
      // Examples of valid elements:
      // <premiered>1994-09-14</premiered>
      // <premiered>1994</premiered>
      return ((CurrentStub.Premiered = ParseSimpleDateTime(element)) != null);
    }

    /// <summary>
    /// Tries to read the year value
    /// </summary>
    /// <param name="element"><see cref="XElement"/> to read from</param>
    /// <returns><c>true</c> if a value was found in <paramref name="element"/>; otherwise <c>false</c></returns>
    private bool TryReadYear(XElement element)
    {
      // Examples of valid elements:
      // <year>1994-09-14</year>
      // <year>1994</year>
      return ((CurrentStub.Year = ParseSimpleDateTime(element)) != null);
    }

    /// <summary>
    /// Tries to read a studio value
    /// </summary>
    /// <param name="element">Element to read from</param>
    /// <returns><c>true</c> if a value was found in <paramref name="element"/>; otherwise <c>false</c></returns>
    private bool TryReadStudio(XElement element)
    {
      // Example of a valid element:
      // <studio>SyFy</studio>
      return ((CurrentStub.Studio = ParseSimpleString(element)) != null);
    }

    /// <summary>
    /// Tries to (asynchronously) read an actor value
    /// </summary>
    /// <param name="element"><see cref="XElement"/> to read from</param>
    /// <param name="nfoDirectoryFsra"><see cref="IFileSystemResourceAccessor"/> to the parent directory of the nfo-file</param>
    /// <returns><c>true</c> if a value was found in <paramref name="element"/>; otherwise <c>false</c></returns>
    private async Task<bool> TryReadActorAsync(XElement element, IFileSystemResourceAccessor nfoDirectoryFsra)
    {
      // For examples of valid element values see the comment in NfoReaderBase.ParsePerson
      var person = await ParsePerson(element, nfoDirectoryFsra);
      if (person == null)
        return false;
      if (CurrentStub.Actors == null)
        CurrentStub.Actors = new HashSet<PersonStub>();
      CurrentStub.Actors.Add(person);
      return true;
    }

    /// <summary>
    /// Tries to read a status value
    /// </summary>
    /// <param name="element">Element to read from</param>
    /// <returns><c>true</c> if a value was found in <paramref name="element"/>; otherwise <c>false</c></returns>
    private bool TryReadStatus(XElement element)
    {
      // Example of a valid element:
      // <status>Continuing</status>
      return ((CurrentStub.Status = ParseSimpleString(element)) != null);
    }

    #endregion

    #region Content information

    /// <summary>
    /// Tries to read the plot value
    /// </summary>
    /// <param name="element"><see cref="XElement"/> to read from</param>
    /// <returns><c>true</c> if a value was found in <paramref name="element"/>; otherwise <c>false</c></returns>
    private bool TryReadPlot(XElement element)
    {
      // Example of a valid element:
      // <plot>This series tells a story about...</plot>
      return ((CurrentStub.Plot = ParseSimpleString(element)) != null);
    }

    /// <summary>
    /// Tries to read the outline value
    /// </summary>
    /// <param name="element"><see cref="XElement"/> to read from</param>
    /// <returns><c>true</c> if a value was found in <paramref name="element"/>; otherwise <c>false</c></returns>
    private bool TryReadOutline(XElement element)
    {
      // Example of a valid element:
      // <outline>This series tells a story about...</outline>
      return ((CurrentStub.Outline = ParseSimpleString(element)) != null);
    }

    /// <summary>
    /// Tries to read the tagline value
    /// </summary>
    /// <param name="element"><see cref="XElement"/> to read from</param>
    /// <returns><c>true</c> if a value was found in <paramref name="element"/>; otherwise <c>false</c></returns>
    private bool TryReadTagline(XElement element)
    {
      // Example of a valid element:
      // <tagline>This series tells a story about...</tagline>
      return ((CurrentStub.Tagline = ParseSimpleString(element)) != null);
    }

    /// <summary>
    /// Tries to read the trailer value
    /// </summary>
    /// <param name="element"><see cref="XElement"/> to read from</param>
    /// <returns><c>true</c> if a value was found in <paramref name="element"/>; otherwise <c>false</c></returns>
    private bool TryReadTrailer(XElement element)
    {
      // Example of a valid element:
      // <trailer>[URL to a trailer]</trailer>
      return ((CurrentStub.Trailer = ParseSimpleString(element)) != null);
    }

    /// <summary>
    /// Tries to read a genre value
    /// </summary>
    /// <param name="element"><see cref="XElement"/> to read from</param>
    /// <returns><c>true</c> if a value was found in <paramref name="element"/>; otherwise <c>false</c></returns>
    private bool TryReadGenre(XElement element)
    {
      // Examples of valid elements:
      // <genre>Horror</genre>
      // <genre>Horror / Trash</genre>
      return ((CurrentStub.Genres = ParseCharacterSeparatedStrings(element, CurrentStub.Genres)) != null);
    }

    /// <summary>
    /// Tries to read a genres value
    /// </summary>
    /// <param name="element"><see cref="XElement"/> to read from</param>
    /// <returns><c>true</c> if a value was found in <paramref name="element"/>; otherwise <c>false</c></returns>
    private bool TryReadGenres(XElement element)
    {
      // Example of a valid element:
      // <genres>
      //  <genre>[genre-value]</genre>
      // </genres>
      // There can be one of more <genre> child elements
      // [genre-value] can be any value that can be read by TryReadGenre
      if (element == null || !element.HasElements)
        return false;
      var result = false;
      foreach (var childElement in element.Elements())
      {
        if (childElement.Name == "genre")
          result = TryReadGenre(childElement) || result;
        else
          DebugLogger.Warn("[#{0}]: Unknown child element: {1}", MiNumber, childElement);
      }
      return result;
    }

    #endregion

    #region Images

    /// <summary>
    /// Tries to (asynchronously) read a thumbnail image
    /// </summary>
    /// <param name="element"><see cref="XElement"/> to read from</param>
    /// <param name="nfoDirectoryFsra"><see cref="IFileSystemResourceAccessor"/> to the parent directory of the nfo-file</param>
    /// <returns><c>true</c> if a value was found in <paramref name="element"/>; otherwise <c>false</c></returns>
    private async Task<bool> TryReadThumbAsync(XElement element, IFileSystemResourceAccessor nfoDirectoryFsra)
    {
      // Example of a valid element:
      // <thumb aspect="[aspect]" type="[type]" season="[season]" colors="[colors]">[ImageString]</thumb>
      //
      // [ImageString]: For examples see the comment of NfoReaderBase.ParseSimpleImageAsync
      // All attributes are optional
      // [aspect]: Can be "banner", "poster" and theoretically "fanart", but instead of an attribute, all fanart thumbs are child elements of <fanart>
      // [type]:  Can be "season" and theoretically "series", but instead of having an attribute "series", the type attribute is left out
      // [season]: Only makes sense if [type]="season". A value of 0 is valid (for specials), a value of -1 is the same as if [type]="series" (or no type attribute)
      // [colors]: Contains three RGB colors in decimal format and pipe delimited (e.g. "|49,56,66|180,167,159|216,216,216|"). These are colors the artist picked
      //           that go well with the image. In order they are Light Accent Color, Dark Accent Color and Neutral Midtone Color. Only shows if [aspect]="fanart"
      //           (or the element is a child of <fanart>).
      var thumb = await ParseSimpleImageAsync(element, nfoDirectoryFsra);
      if (thumb == null)
        return false;
      var result = new SeriesThumbStub { Thumb = thumb };
      
      var aspect = ParseStringAttribute(element, "aspect");
      switch (aspect)
      {
        case null:
          result.Aspect = SeriesThumbStub.ThumbAspect.Fanart;
          break;
        case "banner":
          result.Aspect = SeriesThumbStub.ThumbAspect.Banner;
          break;
        case "poster":
          result.Aspect = SeriesThumbStub.ThumbAspect.Poster;
          break;
        default:
          DebugLogger.Warn("[#{0}]: Unknown aspect attribute in thumb element: {1}", MiNumber, aspect);
          break;
      }

      var type = ParseStringAttribute(element, "type");
      if (type != null)
      {
        if (type == "season")
        {
          var season = ParseIntAttribute(element, "season");
          if (season.HasValue && season.Value >= 0)
            result.Season = season.Value;
        }
        else
          DebugLogger.Warn("[#{0}]: Unknown type attribute in thumb element: {1}", MiNumber, type);
      }

      var colors = ParseStringAttribute(element, "colors");
      if (colors != null)
      {
        var separatedColors = colors.Split('|');
        if (separatedColors.Count() == 5)
        {
          result.LightAccentColor = ParseColor(separatedColors[1]);
          result.DarkAccentColor = ParseColor(separatedColors[2]);
          result.NeutralMidtoneColor = ParseColor(separatedColors[3]);
        }
        else
          DebugLogger.Warn("[#{0}]: Invalid colors attribute in thumb element: {1}", MiNumber, colors);
      }

      if (CurrentStub.Thumbs == null)
        CurrentStub.Thumbs = new HashSet<SeriesThumbStub>();
      CurrentStub.Thumbs.Add(result);
      return true;
    }

    /// <summary>
    /// Example of a valid element:
    /// </summary>
    /// <param name="element"><see cref="XElement"/> to read from</param>
    /// <param name="nfoDirectoryFsra"><see cref="IFileSystemResourceAccessor"/> to the parent directory of the nfo-file</param>
    /// <returns><c>true</c> if a value was found in <paramref name="element"/>; otherwise <c>false</c></returns>
    private async Task<bool> TryReadFanArtAsync(XElement element, IFileSystemResourceAccessor nfoDirectoryFsra)
    {
      // For examples of valid element values c of NfoReaderBase.ParseMultipleImagesAsync
      // <fanart url="http://thetvdb.com/banners/">
      //   [One or more child elements that can be parsed by TryReadThumbAsync]
      // </fanart>
      // The url attribute is optional, but if it is present, its value will be prepended to every child element's content
      if (element == null || !element.HasElements)
        return false;
      var urlPrefix = ParseStringAttribute(element, "url");
      var result = false;
      foreach (var childElement in element.Elements())
      {
        if (childElement.Name != "thumb")
        {
          DebugLogger.Warn("[#{0}]: Invalid child element in fanart element: {1}", MiNumber, childElement);
          continue;
        }
        if (urlPrefix != null)
          childElement.Value = urlPrefix + childElement.Value;
        if (await TryReadThumbAsync(childElement, nfoDirectoryFsra))
          result = true;
      }
      return result;
    }

    #endregion

    #region Certification and ratings

    /// <summary>
    /// Tries to read a mpaa value
    /// </summary>
    /// <param name="element"><see cref="XElement"/> to read from</param>
    /// <returns><c>true</c> if a value was found in <paramref name="element"/>; otherwise <c>false</c></returns>
    private bool TryReadMpaa(XElement element)
    {
      // Examples of valid elements:
      // <mpaa>12</mpaa>
      // <mpaa>DE:FSK 12</mpaa>
      // <mpaa>DE:FSK 12 / DE:FSK12 / DE:12 / DE:ab 12</mpaa>
      return ((CurrentStub.Mpaa = ParseCharacterSeparatedStrings(element, CurrentStub.Mpaa)) != null);
    }

    /// <summary>
    /// Tries to read the rating value
    /// </summary>
    /// <param name="element"><see cref="XElement"/> to read from</param>
    /// <returns><c>true</c> if a value was found in <paramref name="element"/>; otherwise <c>false</c></returns>
    private bool TryReadRating(XElement element)
    {
      // Example of a valid element:
      // <rating>8.5</rating>
      // A value of 0 (zero) is ignored
      var value = ParseSimpleDecimal(element);
      if (value == null || value.Value == decimal.Zero)
        return false;
      CurrentStub.Rating = value;
      return true;
    }

    /// <summary>
    /// Tries to read the votes value
    /// </summary>
    /// <param name="element"><see cref="XElement"/> to read from</param>
    /// <returns><c>true</c> if a value was found in <paramref name="element"/>; otherwise <c>false</c></returns>
    private bool TryReadVotes(XElement element)
    {
      // Example of a valid element:
      // <votes>2941</votes>
      // A value of 0 (zero) is ignored
      var value = ParseSimpleInt(element);
      if (value == 0)
        value = null;
      return ((CurrentStub.Votes = value) != null);
    }

    /// <summary>
    /// Tries to read the top250 value
    /// </summary>
    /// <param name="element"><see cref="XElement"/> to read from</param>
    /// <returns><c>true</c> if a value was found in <paramref name="element"/>; otherwise <c>false</c></returns>
    private bool TryReadTop250(XElement element)
    {
      // Examples of valid elements:
      // <top250>250</top250>
      // <top250>0</top250>
      // A value of 0 (zero) is ignored
      var value = ParseSimpleInt(element);
      if (value == 0)
        value = null;
      return ((CurrentStub.Top250 = value) != null);
    }

    #endregion

    #endregion

    #region General helper methods

    /// <summary>
    /// Ignores the respective element
    /// </summary>
    /// <param name="element"><see cref="XElement"/> to ignore</param>
    /// <returns><c>false</c></returns>
    /// <remarks>
    /// We use this method as TryReadElementDelegate for elements, of which we know that they are irrelevant in the context of a series,
    /// but which are nevertheless contained in some series' nfo-files. Having this method registered as handler delegate avoids that
    /// the respective xml element is logged as unknown element.
    /// </remarks>
    private static bool Ignore(XElement element)
    {
      return false;
    }

    /// <summary>
    /// Parses a string like "20,50,100" into a <see cref="SeriesThumbStub.Color"/> object
    /// </summary>
    /// <param name="colorString">String to parse</param>
    /// <returns>A valid <see cref="SeriesThumbStub.Color"/> object or <c>null</c> if parsing was not possible</returns>
    private SeriesThumbStub.Color ParseColor(string colorString)
    {
      var separatedRgb = colorString.Split(',');
      if (separatedRgb.Count() != 3)
      {
        DebugLogger.Warn("[#{0}]: Invalid colors attribute in thumb element: {1}", MiNumber, colorString);
        return null;
      }
      byte r;
      if (!byte.TryParse(separatedRgb[0], out r))
      {
        DebugLogger.Warn("[#{0}]: Invalid colors attribute in thumb element: {1}", MiNumber, colorString);
        return null;
      }
      byte g;
      if (!byte.TryParse(separatedRgb[1], out g))
      {
        DebugLogger.Warn("[#{0}]: Invalid colors attribute in thumb element: {1}", MiNumber, colorString);
        return null;
      }
      byte b;
      if (!byte.TryParse(separatedRgb[2], out b))
      {
        DebugLogger.Warn("[#{0}]: Invalid colors attribute in thumb element: {1}", MiNumber, colorString);
        return null;
      }
      return new SeriesThumbStub.Color(r, g, b);
    }

    #endregion

    #endregion

    #region Public methods

    /// <summary>
    /// Gets the <see cref="SeriesStub"/> objects generated by this class
    /// </summary>
    /// <returns>List of <see cref="SeriesStub"/> objects</returns>
    public List<SeriesStub> GetSeriesStubs()
    {
      return Stubs;
    }

    #endregion

    #region BaseOverrides

    /// <summary>
    /// Checks whether the <paramref name="itemRootElement"/>'s name is "tvshow"
    /// </summary>
    /// <param name="itemRootElement">Element to check</param>
    /// <returns><c>true</c> if the element's name is "tvshow"; else <c>false</c></returns>
    protected override bool CanReadItemRootElementTree(XElement itemRootElement)
    {
      var itemRootElementName = itemRootElement.Name.ToString();
      if (itemRootElementName == SERIES_ROOT_ELEMENT_NAME)
        return true;
      DebugLogger.Warn("[#{0}]: Cannot extract metadata; name of the item root element is {1} instead of {2}", MiNumber, itemRootElementName, SERIES_ROOT_ELEMENT_NAME);
      return false;
    }

    /// <summary>
    /// Tries to read a series nfo-file into <see cref="SeriesStub"/> objects (or gets them from cache)
    /// </summary>
    /// <param name="nfoFsra"><see cref="IFileSystemResourceAccessor"/> pointing to the nfo-file</param>
    /// <returns><c>true</c> if any usable metadata was found; else <c>false</c></returns>
    public override async Task<bool> TryReadMetadataAsync(IFileSystemResourceAccessor nfoFsra)
    {
      var stubs = await CACHE.GetValue(nfoFsra.CanonicalLocalResourcePath, async path =>
      {
        DebugLogger.Info("[#{0}]: SeriesStub object for series nfo-file not found in cache; parsing nfo-file {1}", MiNumber, nfoFsra.CanonicalLocalResourcePath);
        if (await base.TryReadMetadataAsync(nfoFsra))
        {
          if (_settings.EnableDebugLogging && _settings.WriteStubObjectIntoDebugLog)
            LogStubObjects();
          return Stubs;
        }
        return null;
      });
      if (stubs == null)
        return false;
      Stubs = stubs;
      return true;
    }

    #endregion
  }
}
