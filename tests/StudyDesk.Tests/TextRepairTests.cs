using StudyDesk.Application;

namespace StudyDesk.Tests;

[TestFixture]
public sealed class TextRepairTests
{
    [TestCase("JÃºnior espera instruÃ§Ãµes; VocÃª estÃ¡ travado hÃ¡ 3 horas", "Júnior espera instruções; Você está travado há 3 horas")]
    [TestCase("nÃ£o precisa de cÃ³digo e competÃªncia", "não precisa de código e competência")]
    [TestCase("atÃ© resolver, transiÃ§Ã£o", "até resolver, transição")]
    public void Should_FixMojibake_When_Utf8WasReadAsCp1252(string broken, string expected)
        => Assert.That(TextRepair.Fix(broken), Is.EqualTo(expected));

    [Test]
    public void Should_KeepText_When_AlreadyCorrect()
        => Assert.That(TextRepair.Fix("Júnior não está à vontade — ação"), Is.EqualTo("Júnior não está à vontade — ação"));
}
