using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LibreKO.Common.Migrations
{
    /// <inheritdoc />
    public partial class AddRewardSystems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CharacterRewardQuests",
                columns: table => new
                {
                    CharacterId = table.Column<int>(type: "int", nullable: false),
                    QuestId = table.Column<int>(type: "int", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    Kills = table.Column<int>(type: "int", nullable: false),
                    AcceptedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ClaimedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterRewardQuests", x => new { x.CharacterId, x.QuestId, x.PeriodStart });
                    table.ForeignKey(
                        name: "FK_CharacterRewardQuests_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "DailyRewardClaims",
                columns: table => new
                {
                    AccountId = table.Column<int>(type: "int", nullable: false),
                    Pool = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    CharacterId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    ItemId = table.Column<int>(type: "int", nullable: false),
                    Count = table.Column<int>(type: "int", nullable: false),
                    ClaimedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyRewardClaims", x => new { x.AccountId, x.Pool, x.Day });
                    table.ForeignKey(
                        name: "FK_DailyRewardClaims_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "EventCoinWallets",
                columns: table => new
                {
                    CharacterId = table.Column<int>(type: "int", nullable: false),
                    Coins = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventCoinWallets", x => x.CharacterId);
                    table.ForeignKey(
                        name: "FK_EventCoinWallets_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RewardPrizes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Pool = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    MinLevel = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    MaxLevel = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    Weight = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    ItemId = table.Column<int>(type: "int", nullable: false),
                    Count = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RewardPrizes", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RewardQuests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Board = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    Title = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    MinLevel = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    MaxLevel = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    Recurrence = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    KillCount = table.Column<int>(type: "int", nullable: false),
                    PartyShared = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    StartMonth = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    StartDay = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    DurationDays = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RewardQuests", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RouletteSpins",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    CharacterId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    ItemId = table.Column<int>(type: "int", nullable: false),
                    Count = table.Column<int>(type: "int", nullable: false),
                    SpunAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RouletteSpins", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RouletteSpins_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RewardQuestItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    QuestId = table.Column<int>(type: "int", nullable: false),
                    ItemId = table.Column<int>(type: "int", nullable: false),
                    Count = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RewardQuestItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RewardQuestItems_RewardQuests_QuestId",
                        column: x => x.QuestId,
                        principalTable: "RewardQuests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RewardQuestRewards",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    QuestId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    ItemId = table.Column<int>(type: "int", nullable: false),
                    Count = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RewardQuestRewards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RewardQuestRewards_RewardQuests_QuestId",
                        column: x => x.QuestId,
                        principalTable: "RewardQuests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RewardQuestTargets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    QuestId = table.Column<int>(type: "int", nullable: false),
                    NpcId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RewardQuestTargets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RewardQuestTargets_RewardQuests_QuestId",
                        column: x => x.QuestId,
                        principalTable: "RewardQuests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_Login",
                table: "Accounts",
                column: "Login",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RewardPrizes_Pool",
                table: "RewardPrizes",
                column: "Pool");

            migrationBuilder.CreateIndex(
                name: "IX_RewardQuestItems_QuestId_ItemId",
                table: "RewardQuestItems",
                columns: new[] { "QuestId", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RewardQuestRewards_QuestId",
                table: "RewardQuestRewards",
                column: "QuestId");

            migrationBuilder.CreateIndex(
                name: "IX_RewardQuestTargets_NpcId",
                table: "RewardQuestTargets",
                column: "NpcId");

            migrationBuilder.CreateIndex(
                name: "IX_RewardQuestTargets_QuestId_NpcId",
                table: "RewardQuestTargets",
                columns: new[] { "QuestId", "NpcId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RouletteSpins_CharacterId_SpunAt",
                table: "RouletteSpins",
                columns: new[] { "CharacterId", "SpunAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CharacterRewardQuests");

            migrationBuilder.DropTable(
                name: "DailyRewardClaims");

            migrationBuilder.DropTable(
                name: "EventCoinWallets");

            migrationBuilder.DropTable(
                name: "RewardPrizes");

            migrationBuilder.DropTable(
                name: "RewardQuestItems");

            migrationBuilder.DropTable(
                name: "RewardQuestRewards");

            migrationBuilder.DropTable(
                name: "RewardQuestTargets");

            migrationBuilder.DropTable(
                name: "RouletteSpins");

            migrationBuilder.DropTable(
                name: "RewardQuests");

            migrationBuilder.DropIndex(
                name: "IX_Accounts_Login",
                table: "Accounts");
        }
    }
}
