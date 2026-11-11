using MediaBrowser.Model.Entities;
using StrmAssistant.Mod;
using System.Collections.Generic;
using Xunit;

namespace StrmAssistant.Tests
{
    public class IntroMarkerProtectorTests
    {
        [Fact]
        public void MergeMissingMarkers_AddsMarkersMissingFromIncoming()
        {
            var incoming = new List<ChapterInfo>
            {
                new ChapterInfo { StartPositionTicks = 0 }
            };
            var existing = new List<ChapterInfo>
            {
                new ChapterInfo
                {
                    MarkerType = MarkerType.IntroStart,
                    StartPositionTicks = 10,
                    Name = "IntroStart#DB"
                },
                new ChapterInfo
                {
                    MarkerType = MarkerType.CreditsStart,
                    StartPositionTicks = 100,
                    Name = "CreditsStart#SA"
                }
            };

            var merged = IntroMarkerProtector.MergeMissingMarkers(incoming, existing);

            Assert.Equal(3, merged.Count);
            Assert.Contains(merged, c => c.MarkerType == MarkerType.IntroStart);
            Assert.Contains(merged, c => c.MarkerType == MarkerType.CreditsStart);
            Assert.Equal(0, merged[0].StartPositionTicks);
        }

        [Fact]
        public void MergeMissingMarkers_KeepsIncomingForSameMarkerType()
        {
            var incoming = new List<ChapterInfo>
            {
                new ChapterInfo
                {
                    MarkerType = MarkerType.IntroStart,
                    StartPositionTicks = 20,
                    Name = "IntroStart#DB"
                }
            };
            var existing = new List<ChapterInfo>
            {
                new ChapterInfo
                {
                    MarkerType = MarkerType.IntroStart,
                    StartPositionTicks = 10,
                    Name = "IntroStart#SA"
                },
                new ChapterInfo
                {
                    MarkerType = MarkerType.CreditsStart,
                    StartPositionTicks = 100,
                    Name = "CreditsStart"
                }
            };

            var merged = IntroMarkerProtector.MergeMissingMarkers(incoming, existing);

            Assert.Equal(2, merged.Count);
            Assert.Contains(merged, c =>
                c.MarkerType == MarkerType.IntroStart && c.StartPositionTicks == 20);
            Assert.Contains(merged, c => c.MarkerType == MarkerType.CreditsStart);
        }

        [Fact]
        public void MergeMissingMarkers_NoChangeWhenAllMarkerTypesPresent()
        {
            var incoming = new List<ChapterInfo>
            {
                new ChapterInfo { MarkerType = MarkerType.IntroStart, StartPositionTicks = 10 },
                new ChapterInfo { MarkerType = MarkerType.IntroEnd, StartPositionTicks = 20 },
                new ChapterInfo { MarkerType = MarkerType.CreditsStart, StartPositionTicks = 100 }
            };
            var existing = new List<ChapterInfo>
            {
                new ChapterInfo { MarkerType = MarkerType.IntroStart, StartPositionTicks = 1 },
                new ChapterInfo { MarkerType = MarkerType.CreditsStart, StartPositionTicks = 2 }
            };

            var merged = IntroMarkerProtector.MergeMissingMarkers(incoming, existing);

            Assert.Equal(3, merged.Count);
            Assert.Contains(merged, c => c.StartPositionTicks == 10);
            Assert.DoesNotContain(merged, c => c.StartPositionTicks == 1);
        }

        [Fact]
        public void MergeMissingMarkers_HandlesEmptyExisting()
        {
            var incoming = new List<ChapterInfo>
            {
                new ChapterInfo { StartPositionTicks = 0 }
            };

            var merged = IntroMarkerProtector.MergeMissingMarkers(incoming, new List<ChapterInfo>());

            Assert.Single(merged);
        }
    }
}
