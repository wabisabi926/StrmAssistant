using StrmAssistant.Web.Service;
using System.Text.Json;
using Xunit;

namespace StrmAssistant.Tests
{
    public class ConfigTransferJsonTests
    {
        [Theory]
        [InlineData("values")]
        [InlineData("Values")]
        [InlineData("VALUES")]
        public void FindsValuesElementCaseInsensitively(string propertyName)
        {
            using var document = JsonDocument.Parse(
                $"{{\"{propertyName}\":{{\"PluginOptions.EnablePlugin\":\"true\"}}}}");

            Assert.True(ConfigTransferService.TryGetValuesElement(document.RootElement, out var values));
            Assert.Equal(JsonValueKind.Object, values.ValueKind);
            Assert.Equal("true", values.GetProperty("PluginOptions.EnablePlugin").GetString());
        }

        [Fact]
        public void RejectsNonObjectValues()
        {
            using var document = JsonDocument.Parse("{\"Values\":[]}");

            Assert.True(ConfigTransferService.TryGetValuesElement(document.RootElement, out var values));
            Assert.NotEqual(JsonValueKind.Object, values.ValueKind);
        }
    }
}
