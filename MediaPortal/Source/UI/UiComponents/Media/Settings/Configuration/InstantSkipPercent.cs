#region Copyright (C) 2007-2017 Team MediaPortal

/*
    Copyright (C) 2007-2017 Team MediaPortal
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

using MediaPortal.Common.Configuration.ConfigurationClasses;

namespace MediaPortal.UiComponents.Media.Settings.Configuration
{
  public class InstantSkipPercent : LimitedNumberSelect
  {
    public override void Load()
    {
      base.Load();
      _lowerLimit = 0;
      _upperLimit = 50;
      _type = NumberType.Integer;
      _step = 5;
      _value = SettingsManager.Load<MediaModelSettings>().InstantSkipPercent;
    }

    public override void Save()
    {
      base.Save();
      MediaModelSettings settings = SettingsManager.Load<MediaModelSettings>();
      settings.InstantSkipPercent = _value;
      SettingsManager.Save(settings);
    }
  }
}