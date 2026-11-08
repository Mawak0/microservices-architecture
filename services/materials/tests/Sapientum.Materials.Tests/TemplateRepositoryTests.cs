using System.Net;
using System.Text;
using Sapientum.Materials.Application;
using Sapientum.Materials.Infrastructure.Http;
using Sapientum.Materials.Infrastructure.Templates;
using Xunit;

namespace Sapientum.Materials.Tests;

public sealed class TemplateRepositoryTests
{
    private const string Json = """
        {"key":"new-game/layout","format":"declarative-v1",
         "answerSheet":[{"kind":"field","field":"heading"},{"kind":"qr"},{"kind":"answer-area"}],
         "taskSheet":[{"kind":"field","field":"statement"}]}
        """;
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task OwnedFileCatalogAndHttpAdapterSatisfyTheSameTemplatePort(bool file)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"materials-template-{Guid.NewGuid()}");
        using var client = new HttpClient(new TemplateHandler()) { BaseAddress = new Uri("http://templates/") };
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "new-game")); await File.WriteAllTextAsync(Path.Combine(directory, "new-game/layout.json"), Json);
            ITemplateRepository repository = file ? new FileTemplateRepository(directory) : new HttpTemplateRepository(new(client));
            var first = await repository.GetAsync("new-game/layout", default); var second = await repository.GetAsync("new-game/layout", default);
            Assert.Equal("new-game/layout", first.Key); Assert.Equal(first.AnswerSheet, second.AnswerSheet); Assert.Equal(first.TaskSheet, second.TaskSheet);
            Assert.Equal(3, first.AnswerSheet.Count);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact] public async Task FileCatalogRejectsTraversalAndUnknownFiles()
    {
        var repository = new FileTemplateRepository(Path.Combine(Path.GetTempPath(), $"materials-catalog-{Guid.NewGuid()}"));
        await Assert.ThrowsAsync<Sapientum.Materials.Domain.DomainException>(() => repository.GetAsync("../outside", default));
        await Assert.ThrowsAsync<NotFoundException>(() => repository.GetAsync("unknown/layout", default));
    }
    private sealed class TemplateHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json, Encoding.UTF8, "application/json") });
    }
}
