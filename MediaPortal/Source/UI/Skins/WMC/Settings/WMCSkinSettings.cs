﻿using MediaPortal.Common.Settings;
using SkinSettings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MediaPortal.UiComponents.WMCSkin.Settings
{
  public class WMCSkinSettings
  {
    public const string SKIN_NAME = "WMCSkin";

    [Setting(SettingScope.User, true)]
    public bool EnableFanart { get; set; }

    [Setting(SettingScope.User, true)]
    public bool EnableListWatchedFlags { get; set; }

    [Setting(SettingScope.User, true)]
    public bool EnableGridWatchedFlags { get; set; }

    [Setting(SettingScope.User, true)]
    public bool EnableCoverWatchedFlags { get; set; }
  }
}
