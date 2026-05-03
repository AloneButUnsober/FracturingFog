using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace FracturingFog.Models
{
    public enum ResolutionType
    {
        Full, // 5:4(1.25), 4:3(1.3)
        Wide, // 16:10(1.6), 15:9(1.6), 16:9(1.775-1.81)
        UltraWide // 18:9(2.0-2.2), 32:9(3.5)
    }

    public enum ResolutionRatio
    {
        FiveToFour,
        FourToThree,
        ThreeToTwo,
        SixteenToTen,
        FifteenToNine,
        SixteenToNine,
        SeventeenToNine,
        EighteenToNine,
        TwentyOneToNine,
        ThirtyTwoToNine,
    }

    public class Resolution
    {
        public ResolutionType ResolutionType;
        public ResolutionRatio ResolutionRatio;
        public int Width;
        public int Height;
        public string? Name;
    }

    public static class ResolutionDimensions
    {
        public static Dictionary<ResolutionType, string> ResolutionTypeName = new Dictionary<ResolutionType, string>()
        {
            {ResolutionType.Full, "Full" },
            {ResolutionType.Wide, "Wide" },
            {ResolutionType.UltraWide, "Ultra-Wide" }
        };

        public static Dictionary<ResolutionRatio, string> RatioDescription = new Dictionary<ResolutionRatio, string>()
        {
            { ResolutionRatio.FiveToFour, "5:4" },
            { ResolutionRatio.FourToThree, "4:3" },
            { ResolutionRatio.ThreeToTwo, "3:2" },
            { ResolutionRatio.SixteenToTen, "16:10" },
            { ResolutionRatio.FifteenToNine, "15:9" },
            { ResolutionRatio.SixteenToNine, "16:9" },
            { ResolutionRatio.SeventeenToNine, "17:9" },
            { ResolutionRatio.EighteenToNine, "18:9" },
            { ResolutionRatio.TwentyOneToNine, "21:9" },
            { ResolutionRatio.ThirtyTwoToNine, "32:9" }
        };

        public static Dictionary<ResolutionRatio, ResolutionType> ResolutionTypeRatio = new Dictionary<ResolutionRatio, ResolutionType>()
        {
            { ResolutionRatio.FiveToFour, ResolutionType.Full },
            { ResolutionRatio.FourToThree, ResolutionType.Full },
            { ResolutionRatio.ThreeToTwo, ResolutionType.Wide },
            { ResolutionRatio.SixteenToTen, ResolutionType.Wide },
            { ResolutionRatio.FifteenToNine, ResolutionType.Wide },
            { ResolutionRatio.SixteenToNine, ResolutionType.Wide },
            { ResolutionRatio.SeventeenToNine, ResolutionType.Wide },
            { ResolutionRatio.EighteenToNine, ResolutionType.UltraWide },
            { ResolutionRatio.TwentyOneToNine, ResolutionType.UltraWide },
            { ResolutionRatio.ThirtyTwoToNine, ResolutionType.UltraWide },
        };

        public static Resolution[] Resolutions =
            [
                new()
                {
                 Name = "1280x1024",
                 ResolutionType = ResolutionType.Full,
                 ResolutionRatio = ResolutionRatio.FiveToFour,
                 Width = 1280,
                 Height = 1024,
                },
                new()
                {
                 Name = "1280x1080",
                 ResolutionType = ResolutionType.Full,
                 ResolutionRatio = ResolutionRatio.FiveToFour,
                 Width = 1280,
                 Height = 1080,
                },
                new()
                {
                 Name = "2560x2048",
                 ResolutionType = ResolutionType.Full,
                 ResolutionRatio = ResolutionRatio.FiveToFour,
                 Width = 2560,
                 Height = 2048,
                },
                new()
                {
                 Name = "160x120",
                 ResolutionType = ResolutionType.Full,
                 ResolutionRatio = ResolutionRatio.FourToThree,
                 Width = 160,
                 Height = 120,
                },
                new()
                {
                 Name = "320x240",
                 ResolutionType = ResolutionType.Full,
                 ResolutionRatio = ResolutionRatio.FourToThree,
                 Width = 320,
                 Height = 240,
                },
                new()
                {
                 Name = "640x480",
                 ResolutionType = ResolutionType.Full,
                 ResolutionRatio = ResolutionRatio.FourToThree,
                 Width = 640,
                 Height = 480,
                },
                new()
                {
                 Name = "800x600",
                 ResolutionType = ResolutionType.Full,
                 ResolutionRatio = ResolutionRatio.FourToThree,
                 Width = 800,
                 Height = 600,
                },
                new()
                {
                 Name = "960x720",
                 ResolutionType = ResolutionType.Full,
                 ResolutionRatio = ResolutionRatio.FourToThree,
                 Width = 960,
                 Height = 720,
                },
                new()
                {
                 Name = "1024x768",
                 ResolutionType = ResolutionType.Full,
                 ResolutionRatio = ResolutionRatio.FourToThree,
                 Width = 1024,
                 Height = 768,
                },
                new()
                {
                 Name = "1280x960",
                 ResolutionType = ResolutionType.Full,
                 ResolutionRatio = ResolutionRatio.FourToThree,
                 Width = 1280,
                 Height = 960,
                },
                new()
                {
                 Name = "1400x1080",
                 ResolutionType = ResolutionType.Full,
                 ResolutionRatio = ResolutionRatio.FourToThree,
                 Width = 1400,
                 Height = 1080,
                },
                new()
                {
                 Name = "2048x1536",
                 ResolutionType = ResolutionType.Full,
                 ResolutionRatio = ResolutionRatio.FourToThree,
                 Width = 2048,
                 Height = 1536,
                },
                new()
                {
                 Name = "240x160",
                 ResolutionType = ResolutionType.Wide,
                 ResolutionRatio = ResolutionRatio.ThreeToTwo,
                 Width = 240,
                 Height = 160,
                },
                new()
                {
                 Name = "360x240",
                 ResolutionType = ResolutionType.Wide,
                 ResolutionRatio = ResolutionRatio.ThreeToTwo,
                 Width = 360,
                 Height = 240,
                },
                new()
                {
                 Name = "480x320",
                 ResolutionType = ResolutionType.Wide,
                 ResolutionRatio = ResolutionRatio.ThreeToTwo,
                 Width = 480,
                 Height = 320,
                },
                new()
                {
                 Name = "720x480",
                 ResolutionType = ResolutionType.Wide,
                 ResolutionRatio = ResolutionRatio.ThreeToTwo,
                 Width = 720,
                 Height = 480,
                },
                new()
                {
                 Name = "960x640",
                 ResolutionType = ResolutionType.Wide,
                 ResolutionRatio = ResolutionRatio.ThreeToTwo,
                 Width = 960,
                 Height = 640,
                },
                new()
                {
                 Name = "1440x960",
                 ResolutionType = ResolutionType.Wide,
                 ResolutionRatio = ResolutionRatio.ThreeToTwo,
                 Width = 1440,
                 Height = 960,
                },
                new()
                {
                 Name = "1600x1024",
                 ResolutionType = ResolutionType.Wide,
                 ResolutionRatio = ResolutionRatio.ThreeToTwo,
                 Width = 1600,
                 Height = 1024,
                },
                new()
                {
                 Name = "1920x1200",
                 ResolutionType = ResolutionType.Wide,
                 ResolutionRatio = ResolutionRatio.ThreeToTwo,
                 Width = 1920,
                 Height = 1200,
                },
                new()
                {
                 Name = "2160x1440",
                 ResolutionType = ResolutionType.Wide,
                 ResolutionRatio = ResolutionRatio.ThreeToTwo,
                 Width = 2160,
                 Height = 1440,
                },
                new()
                {
                 Name = "3840x2048",
                 ResolutionType = ResolutionType.Wide,
                 ResolutionRatio = ResolutionRatio.ThreeToTwo,
                 Width = 3840,
                 Height = 2048,
                },
                new()
                {
                 Name = "432x240",
                 ResolutionType = ResolutionType.Wide,
                 ResolutionRatio = ResolutionRatio.EighteenToNine,
                 Width = 432,
                 Height = 240,
                },
                new()
                {
                 Name = "854x480",
                 ResolutionType = ResolutionType.Wide,
                 ResolutionRatio = ResolutionRatio.EighteenToNine,
                 Width = 854,
                 Height = 480,
                },
                new()
                {
                 Name = "960x540",
                 ResolutionType = ResolutionType.Wide,
                 ResolutionRatio = ResolutionRatio.EighteenToNine,
                 Width = 960,
                 Height = 540,
                },
                new()
                {
                 Name = "1024x576",
                 ResolutionType = ResolutionType.Wide,
                 ResolutionRatio = ResolutionRatio.EighteenToNine,
                 Width = 1024,
                 Height = 576,
                },
                new()
                {
                 Name = "1024x600",
                 ResolutionType = ResolutionType.Wide,
                 ResolutionRatio = ResolutionRatio.EighteenToNine,
                 Width = 1024,
                 Height = 600,
                },
                new()
                {
                 Name = "1136x640",
                 ResolutionType = ResolutionType.Wide,
                 ResolutionRatio = ResolutionRatio.EighteenToNine,
                 Width = 1136,
                 Height = 640,
                },
                new()
                {
                 Name = "1280x720",
                 ResolutionType = ResolutionType.Wide,
                 ResolutionRatio = ResolutionRatio.EighteenToNine,
                 Width = 1280,
                 Height = 720,
                },
                new()
                {
                 Name = "1360x768",
                 ResolutionType = ResolutionType.Wide,
                 ResolutionRatio = ResolutionRatio.EighteenToNine,
                 Width = 1360,
                 Height = 768,
                },
                new()
                {
                 Name = "1600x900",
                 ResolutionType = ResolutionType.Wide,
                 ResolutionRatio = ResolutionRatio.EighteenToNine,
                 Width = 1600,
                 Height = 900,
                },
                new()
                {
                 Name = "1920x1080",
                 ResolutionType = ResolutionType.Wide,
                 ResolutionRatio = ResolutionRatio.EighteenToNine,
                 Width = 1920,
                 Height = 1080,
                },
                new()
                {
                 Name = "2048x1152",
                 ResolutionType = ResolutionType.Wide,
                 ResolutionRatio = ResolutionRatio.EighteenToNine,
                 Width = 2048,
                 Height = 1152,
                },
                new()
                {
                 Name = "2560x1440",
                 ResolutionType = ResolutionType.Wide,
                 ResolutionRatio = ResolutionRatio.EighteenToNine,
                 Width = 2560,
                 Height = 1440,
                },
                new()
                {
                 Name = "2880x1620",
                 ResolutionType = ResolutionType.Wide,
                 ResolutionRatio = ResolutionRatio.EighteenToNine,
                 Width = 2880,
                 Height = 1620,
                },
                new()
                {
                 Name = "3200x1800",
                 ResolutionType = ResolutionType.Wide,
                 ResolutionRatio = ResolutionRatio.EighteenToNine,
                 Width = 3200,
                 Height = 1800,
                },
                new()
                {
                 Name = "3840x2160",
                 ResolutionType = ResolutionType.Wide,
                 ResolutionRatio = ResolutionRatio.EighteenToNine,
                 Width = 3840,
                 Height = 2160,
                },
                new()
                {
                 Name = "5120x2880",
                 ResolutionType = ResolutionType.Wide,
                 ResolutionRatio = ResolutionRatio.EighteenToNine,
                 Width = 5120,
                 Height = 2880,
                },
                new()
                {
                 Name = "7680x4320",
                 ResolutionType = ResolutionType.Wide,
                 ResolutionRatio = ResolutionRatio.EighteenToNine,
                 Width = 7680,
                 Height = 4320,
                },
                new()
                {
                 Name = "2560x1080",
                 ResolutionType = ResolutionType.UltraWide,
                 ResolutionRatio = ResolutionRatio.TwentyOneToNine,
                 Width = 2560,
                 Height = 1080,
                },
                new()
                {
                 Name = "3440x1440",
                 ResolutionType = ResolutionType.UltraWide,
                 ResolutionRatio = ResolutionRatio.TwentyOneToNine,
                 Width = 3440,
                 Height = 1440,
                },
                new()
                {
                 Name = "3840x1600",
                 ResolutionType = ResolutionType.UltraWide,
                 ResolutionRatio = ResolutionRatio.TwentyOneToNine,
                 Width = 3840,
                 Height = 1600,
                },
                new()
                {
                 Name = "5120x2160",
                 ResolutionType = ResolutionType.UltraWide,
                 ResolutionRatio = ResolutionRatio.TwentyOneToNine,
                 Width = 5120,
                 Height = 2160,
                },
                new()
                {
                 Name = "10240x4320",
                 ResolutionType = ResolutionType.UltraWide,
                 ResolutionRatio = ResolutionRatio.TwentyOneToNine,
                 Width = 10240,
                 Height = 4320,
                },
                new()
                {
                 Name = "3840x1080",
                 ResolutionType = ResolutionType.UltraWide,
                 ResolutionRatio = ResolutionRatio.ThirtyTwoToNine,
                 Width = 3840,
                 Height = 1080,
                },
                new()
                {
                 Name = "5120x1440",
                 ResolutionType = ResolutionType.UltraWide,
                 ResolutionRatio = ResolutionRatio.ThirtyTwoToNine,
                 Width = 5120,
                 Height = 1440,
                },
                new()
                {
                 Name = "7680x2160",
                 ResolutionType = ResolutionType.UltraWide,
                 ResolutionRatio = ResolutionRatio.ThirtyTwoToNine,
                 Width = 7680,
                 Height = 2160,
                },
            ];

        public static int GetLongestResolutionName()
        {
            int m = 0;
            foreach ( var kvp in Resolutions )
                if (kvp?.Name?.Length > m) m = kvp.Name.Length; 
            return m;
        }
    }
}
