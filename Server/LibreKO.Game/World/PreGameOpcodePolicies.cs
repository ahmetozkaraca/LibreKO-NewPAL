using System.Collections.Frozen;
using LibreKO.Common.Infrastructure.Network;

namespace LibreKO.Game.World;

public static class PreGameOpcodePolicies
{
    public static readonly TokenRate ClientTotal = new(30, 5);
    public static readonly TokenRate Default = new(10, 2);

    private static readonly TokenRate Credentials = new(3, 0.2);
    private static readonly TokenRate CharacterChange = new(5, 0.5);
    private static readonly TokenRate Appearance = new(2, 0.1);
    private static readonly TokenRate Selection = new(5, 1);
    private static readonly TokenRate Listing = new(10, 1);

    private static readonly FrozenDictionary<GameOpcodes, TokenRate> Limits = new Dictionary<GameOpcodes, TokenRate>
    {
        [GameOpcodes.GS_LOGIN] = Credentials,
        [GameOpcodes.GS_KICKOUT] = Credentials,
        [GameOpcodes.GS_NATION_SELECT] = CharacterChange,
        [GameOpcodes.GS_CREATE_CHARACTER] = CharacterChange,
        [GameOpcodes.GS_DELETE_CHARACTER] = CharacterChange,
        [GameOpcodes.GS_CHANGE_HAIR] = Appearance,
        [GameOpcodes.GS_SELECT_CHARACTER] = Selection,
        [GameOpcodes.GS_ALLCHAR_INFO_REQ] = Listing,
        [GameOpcodes.GS_LOADING_LOGIN] = Listing,
    }.ToFrozenDictionary();

    public static TokenRate LimitOf(GameOpcodes opcode) => Limits.GetValueOrDefault(opcode, Default);
}
