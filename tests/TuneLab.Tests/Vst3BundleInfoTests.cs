using System;
using System.IO;
using TuneLab.ExperimentalPatch.InsertHost;
using Xunit;

namespace TuneLab.Tests;

// VST3 bundle 的厂商读取：Contents\Resources\moduleinfo.json 里的 Factory Info/Vendor。
//
// VST3 二进制里只有子类别（"Fx|Delay"），厂商不在那里；VST2 是桥探 effGetVendorString 拿到的。
// 少了这一步，同一个产品的 VST3 版在 FX 菜单里全挂"未知厂商"，而 VST2 版有厂商 —— 落差就在这。
public class Vst3BundleInfoTests
{
    [Fact]
    public void ReadsVendorFromFactoryInfo()
    {
        const string json = """
        {
          "Name": "Room Reverb",
          "Factory Info": {
            "Vendor": "ElephantDSP.com",
            "URL": "https://www.ElephantDSP.com",
          },
          "Classes": [
            { "CID": "ABCD", "Sub Categories": ["Fx", "Reverb"] },
          ],
        }
        """;

        Assert.Equal("ElephantDSP.com", Vst3BundleInfo.ReadVendorFromJson(json));
    }

    [Fact]
    public void TrailingCommasDoNotBreakIt()
    {
        // 插件自带的 build 脚本生成的 moduleinfo.json 常常带尾逗号，严格 JSON 解析会整个失败。
        const string json = """
        {
            "Factory Info": {
                "Vendor": "Damien Zammit",
                "URL": "http://www.zamaudio.com",
                "E-Mail": "a@b.c",
                "Flags": { "Unicode": true },
            },
            "Classes": [
                { "CID": "1", "Sub Categories": ["Fx", "Dynamics"], },
            ],
        }
        """;

        Assert.Equal("Damien Zammit", Vst3BundleInfo.ReadVendorFromJson(json));
    }

    [Fact]
    public void IgnoresClassLevelVendor()
    {
        // 类里也有 Vendor（每个类重复一遍）。取到 factory 级才对 —— 类级取错也只是巧合，
        // 但 factory 级才是"这个产品的厂商"。
        const string json = """
        {
          "Classes": [
            { "Name": "X", "Vendor": "Class Vendor" },
          ],
          "Factory Info": { "Vendor": "Factory Vendor" },
        }
        """;

        Assert.Equal("Factory Vendor", Vst3BundleInfo.ReadVendorFromJson(json));
    }

    [Fact]
    public void FallsBackToAnyVendorKey()
    {
        // 有的 build 脚本不写 "Factory Info" 这一层。
        const string json = """
        { "Vendor": "SomeBody" }
        """;

        Assert.Equal("SomeBody", Vst3BundleInfo.ReadVendorFromJson(json));
    }

    [Fact]
    public void MissingVendorIsNotAnError()
    {
        Assert.Equal(string.Empty, Vst3BundleInfo.ReadVendorFromJson("{ \"Factory Info\": { \"URL\": \"x\" } }"));
        Assert.Equal(string.Empty, Vst3BundleInfo.ReadVendorFromJson(""));
        Assert.Equal(string.Empty, Vst3BundleInfo.ReadVendorFromJson("not json at all"));
    }

    [Fact]
    public void MissingFileReturnsEmpty()
    {
        Assert.Equal(string.Empty, Vst3BundleInfo.VendorOf(Path.Combine(Path.GetTempPath(), "no-such-plugin" + Guid.NewGuid().ToString("N") + ".vst3")));
    }
}
