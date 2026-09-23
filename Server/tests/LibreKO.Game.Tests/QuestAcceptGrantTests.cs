using FluentAssertions;
using LibreKO.Quests;
using LibreKO.Quests.Runtime;
using NSubstitute;

namespace LibreKO.Game.Tests;

public class QuestAcceptGrantTests
{
    private const int QuestId = 1241;
    private const int GrantedItem = 900670000;
    private const int Abandoned = 4;

    private const string Source = """
        Bind Npc 25072 Zone 2
        Quest 1241 "Pocket money"
            Give 1 of 900670000 on accept
            Collect 1 of 900659000

        Rewards
            Give 100 experience
        """;

    [Fact]
    public void ReacceptingAnAbandonedQuestDoesNotHandOverASecondCopy()
    {
        var (program, host) = Compile();
        var held = 0;
        host.ItemCount(GrantedItem).Returns(_ => held);
        host.When(h => h.GiveItem(GrantedItem, Arg.Any<int>(), Arg.Any<int>()))
            .Do(c => held += c.ArgAt<int>(1));

        Accept(program, host);
        host.SetQuestState(QuestId, Abandoned);
        Accept(program, host);

        host.Received(1).GiveItem(GrantedItem, 1, Arg.Any<int>());
        held.Should().Be(1);
    }

    [Fact]
    public void ReacceptingAfterLosingTheItemHandsItOverAgain()
    {
        var (program, host) = Compile();
        host.ItemCount(GrantedItem).Returns(0);

        Accept(program, host);
        host.SetQuestState(QuestId, Abandoned);
        Accept(program, host);

        host.Received(2).GiveItem(GrantedItem, 1, Arg.Any<int>());
    }

    private static (QuestProgram Program, IQuestHost Host) Compile()
    {
        var compilation = QuestCompilation.Create(Source, "grant.quest");
        compilation.Succeeded.Should().BeTrue(compilation.RenderDiagnostics());
        var program = QuestProgramComposer.Compose("grant", 25072, 2, [compilation.Program]);

        var host = Substitute.For<IQuestHost>();
        host.PlayerZone.Returns(2);
        host.PlayerLevel.Returns(40);
        host.When(h => h.SetQuestState(QuestId, Arg.Any<int>()))
            .Do(c => host.QuestStatus(QuestId).Returns(c.ArgAt<int>(1)));
        return (program, host);
    }

    private static void Accept(QuestProgram program, IQuestHost host)
    {
        program.TryGetEntry(QuestProgram.AcceptEvent, QuestId, out var entry).Should().BeTrue();
        new QuestInterpreter(program, host).Run(entry).Failure.Should().BeNull();
    }
}
