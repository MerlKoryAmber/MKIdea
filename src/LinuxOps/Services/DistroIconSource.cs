using System.Windows;
using System.Windows.Media;
using LinuxOps.Models;

namespace LinuxOps.Services;

public static class DistroIconSource
{
    public static ImageSource ForHint(string? hint)
    {
        var key = KeyFor(hint);
        if (Application.Current?.TryFindResource(key) is ImageSource img)
            return img;
        return (ImageSource)Application.Current!.FindResource("DistroIcon.Unknown");
    }

    public static string KeyFor(string? hint)
    {
        var label = DistroLabel.FromHint(hint);
        return label switch
        {
            "Ubuntu" => "DistroIcon.Ubuntu",
            "Debian" => "DistroIcon.Debian",
            "RHEL" => "DistroIcon.RHEL",
            "CentOS" => "DistroIcon.CentOS",
            "Alma" => "DistroIcon.Alma",
            "SUSE" => "DistroIcon.SUSE",
            "Rocky" => "DistroIcon.Rocky",
            "Fedora" => "DistroIcon.Fedora",
            _ => "DistroIcon.Unknown"
        };
    }
}
