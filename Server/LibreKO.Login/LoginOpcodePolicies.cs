using LibreKO.Common.Infrastructure.Network;

namespace LibreKO.Login;

public static class LoginOpcodePolicies
{
    public static readonly TokenRate ClientTotal = new(20, 2);
    public static readonly TokenRate Default = new(10, 1);

    private static readonly TokenRate Credentials = new(3, 0.2);

    public static TokenRate LimitOf(LoginOpcodes opcode) =>
        opcode is LoginOpcodes.LS_LOGIN or LoginOpcodes.LS_MGAME_LOGIN ? Credentials : Default;
}
