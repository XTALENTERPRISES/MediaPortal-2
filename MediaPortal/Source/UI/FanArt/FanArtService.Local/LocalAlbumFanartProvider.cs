#region Copyright (C) 2007-2018 Team MediaPortal

/*
    Copyright (C) 2007-2018 Team MediaPortal
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
using MediaPortal.Backend.MediaLibrary;
using MediaPortal.Common;
using MediaPortal.Common.FanArt;
using MediaPortal.Common.Logging;
using MediaPortal.Common.MediaManagement;
using MediaPortal.Common.MediaManagement.DefaultItemAspects;
using MediaPortal.Common.MediaManagement.MLQueries;
using MediaPortal.Common.ResourceAccess;
using MediaPortal.Common.Services.ResourceAccess;
using MediaPortal.Extensions.UserServices.FanArtService.Interfaces;

namespace MediaPortal.Extensions.UserServices.FanArtService.Local
{
  public class LocalAlbumFanartProvider : IFanArtProvider
  {
    private readonly static Guid[] NECESSARY_MIAS = { AudioAspect.ASPECT_ID, ProviderResourceAspect.ASPECT_ID };

    public FanArtProviderSource Source { get { return FanArtProviderSource.File; } }

    /// <summary>
    /// Gets a list of <see cref="FanArtImage"/>s for a requested <paramref name="mediaType"/>, <paramref name="fanArtType"/> and <paramref name="name"/>.
    /// The name can be: Series name, Actor name, Artist name depending on the <paramref name="mediaType"/>.
    /// </summary>
    /// <param name="mediaType">Requested FanArtMediaType</param>
    /// <param name="fanArtType">Requested FanArtType</param>
    /// <param name="name">Requested name of Series, Actor, Artist...</param>
    /// <param name="maxWidth">Maximum width for image. <c>0</c> returns image in original size.</param>
    /// <param name="maxHeight">Maximum height for image. <c>0</c> returns image in original size.</param>
    /// <param name="singleRandom">If <c>true</c> only one random image URI will be returned</param>
    /// <param name="result">Result if return code is <c>true</c>.</param>
    /// <returns><c>true</c> if at least one match was found.</returns>
    public bool TryGetFanArt(string mediaType, string fanArtType, string name, int maxWidth, int maxHeight, bool singleRandom, out IList<IResourceLocator> result)
    {
      result = null;
      Guid mediaItemId;

      if (mediaType != FanArtMediaTypes.Album && mediaType != FanArtMediaTypes.Audio)
        return false;

      // Don't try to load "fanart" for images
      if (!Guid.TryParse(name, out mediaItemId) || mediaType == FanArtMediaTypes.Image)
        return false;

      IMediaLibrary mediaLibrary = ServiceRegistration.Get<IMediaLibrary>(false);
      if (mediaLibrary == null)
        return false;

      IFilter filter = null;
      IList<MediaItem> items = null;
      List<Guid> necessaryMias = new List<Guid>(NECESSARY_MIAS);
      if (mediaType == FanArtMediaTypes.Album)
      {
        filter = new RelationshipFilter(AudioAspect.ROLE_TRACK, AudioAlbumAspect.ROLE_ALBUM, mediaItemId);
      }
      else if (mediaType == FanArtMediaTypes.Audio)
      {
        //Might be a request for track cover which doesn't exist. Album cover is used instead.
        filter = new MediaItemIdFilter(mediaItemId);
      }
      else
        return false;

      MediaItemQuery mediaQuery = new MediaItemQuery(necessaryMias, filter);
      mediaQuery.Limit = 1;
      items = mediaLibrary.Search(mediaQuery, false, null, false);
      if (items == null || items.Count == 0)
        return false;

      MediaItem mediaItem = items.First();
      // Virtual resources won't have any local fanart
      if (mediaItem.IsVirtual)
        return false;
      var mediaIteamLocator = mediaItem.GetResourceLocator();
      var fanArtPaths = new List<ResourcePath>();
      var files = new List<IResourceLocator>();
      // File based access
      try
      {
        var mediaItemPath = mediaIteamLocator.NativeResourcePath;
        var mediaItemDirectoryPath = ResourcePathHelper.Combine(mediaItemPath, "../");
        string album = null;
        if (MediaItemAspect.TryGetAttribute(mediaItem.Aspects, AudioAspect.ATTR_ALBUM, out album) && LocalFanartHelper.IsDiscFolder(album, mediaItemDirectoryPath.FileName))
        {
          //Probably a CD folder so try next parent
          mediaItemDirectoryPath = ResourcePathHelper.Combine(mediaItemPath, "../../");
        }
        var mediaItemFileNameWithoutExtension = ResourcePathHelper.GetFileNameWithoutExtension(mediaItemPath.ToString()).ToLowerInvariant();
        var mediaItemExtension = ResourcePathHelper.GetExtension(mediaItemPath.ToString());

        using (var directoryRa = new ResourceLocator(mediaIteamLocator.NativeSystemId, mediaItemDirectoryPath).CreateAccessor())
        {
          var directoryFsra = directoryRa as IFileSystemResourceAccessor;
          if (directoryFsra != null)
          {
            var potentialFanArtFiles = LocalFanartHelper.GetPotentialFanArtFiles(directoryFsra);

            if (fanArtType == FanArtTypes.Poster || fanArtType == FanArtTypes.Cover || fanArtType == FanArtTypes.Thumbnail)
            {
              fanArtPaths.AddRange(LocalFanartHelper.FilterPotentialFanArtFilesByName(potentialFanArtFiles, LocalFanartHelper.POSTER_FILENAMES));
              fanArtPaths.AddRange(LocalFanartHelper.FilterPotentialFanArtFilesByName(potentialFanArtFiles, LocalFanartHelper.THUMB_FILENAMES));
            }

            if (fanArtType == FanArtTypes.DiscArt)
              fanArtPaths.AddRange(LocalFanartHelper.FilterPotentialFanArtFilesByName(potentialFanArtFiles, LocalFanartHelper.DISCART_FILENAMES));

            if (fanArtType == FanArtTypes.FanArt)
            {
              fanArtPaths.AddRange(LocalFanartHelper.FilterPotentialFanArtFilesByPrefix(potentialFanArtFiles, LocalFanartHelper.BACKDROP_FILENAMES));

              if (directoryFsra.ResourceExists("ExtraFanArt/"))
                using (var extraFanArtDirectoryFsra = directoryFsra.GetResource("ExtraFanArt/"))
                  fanArtPaths.AddRange(LocalFanartHelper.GetPotentialFanArtFiles(extraFanArtDirectoryFsra));
            }

            files.AddRange(fanArtPaths.Select(path => new ResourceLocator(mediaIteamLocator.NativeSystemId, path)));
          }
        }
      }
      catch (Exception ex)
      {
#if DEBUG
        ServiceRegistration.Get<ILogger>().Warn("LocalAlbumFanArtProvider: Error while searching fanart of type '{0}' for '{1}'", ex, fanArtType, mediaIteamLocator);
#endif
      }
      result = files;
      return files.Count > 0;
    }
  }
}
