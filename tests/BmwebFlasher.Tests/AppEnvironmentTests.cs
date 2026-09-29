using Xunit;

namespace BmwebFlasher.Tests
{
    public class AppEnvironmentTests
    {
        [Theory]
        [InlineData("development", true)]
        [InlineData("Development", true)]
        [InlineData(" dev ", true)]
        [InlineData("production", false)]
        [InlineData("staging", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void OnlyDevelopmentValuesUnlock(string value, bool expected)
        {
            Assert.Equal(expected, AppEnvironment.IsDevelopmentValue(value));
        }

        [Fact]
        public void ParsesKeysCommentsQuotesAndExport()
        {
            var values = AppEnvironment.Parse(new[]
            {
                "# a comment",
                "",
                "BMWEB_ENV=development",
                "export OTHER = \"quoted value\"",
                "SINGLE='x'",
                "no equals sign",
                "=missing key",
            });

            Assert.Equal("development", values["BMWEB_ENV"]);
            Assert.Equal("quoted value", values["OTHER"]);
            Assert.Equal("x", values["SINGLE"]);
            Assert.Equal(3, values.Count);
        }

        [Fact]
        public void LaterLinesWin()
        {
            var values = AppEnvironment.Parse(new[] { "BMWEB_ENV=development", "BMWEB_ENV=production" });
            Assert.Equal("production", values["BMWEB_ENV"]);
        }
    }
}
