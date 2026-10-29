using System.Linq;
using Content.Shared._Harvest.Arcade.RobustOfficer;

namespace Content.Server._Harvest.Arcade.RobustOfficer;

/// <summary>
/// The gun that comes with a rank.
/// </summary>
public sealed record RobustOfficerWeapon(
    RobustOfficerSpriteKind Projectile,
    float Cooldown,
    int Damage,
    float Speed,
    int Pellets,
    float SpreadDegrees,
    float Range,
    bool Pierce)
{
    public static readonly RobustOfficerWeapon Disabler = new(RobustOfficerSpriteKind.BoltDisabler, 0.3f, 1, 11f, 1, 0f, 12f, false);
    public static readonly RobustOfficerWeapon Pistol = new(RobustOfficerSpriteKind.Bullet, 0.22f, 1, 13f, 1, 0f, 12f, false);
    public static readonly RobustOfficerWeapon Shotgun = new(RobustOfficerSpriteKind.Pellet, 0.45f, 1, 12f, 4, 24f, 6.5f, false);
    public static readonly RobustOfficerWeapon EnergyGun = new(RobustOfficerSpriteKind.Laser, 0.2f, 2, 15f, 1, 0f, 14f, true);

    public static RobustOfficerWeapon For(RobustOfficerRank rank)
    {
        return rank switch
        {
            RobustOfficerRank.Officer => Pistol,
            RobustOfficerRank.Warden => Shotgun,
            RobustOfficerRank.HeadOfSecurity => EnergyGun,
            _ => Disabler,
        };
    }
}

/// <summary>
/// What a game keeps from one room to the next: score, lives, credits, rank, upgrades and the stocked item.
/// Also sells the upgrades.
/// </summary>
public sealed class RobustOfficerRun
{
    public const int MaxLives = 9;

    private static readonly int[] PromotionPrices = [40, 80, 130];
    private static readonly int[] FireRatePrices = [15, 25, 40];
    private static readonly int[] DamagePrices = [30, 60];
    private static readonly int[] BootsPrices = [12, 24];
    /// <summary>
    /// Price of the first donut, doubled with each one bought in the game.
    /// </summary>
    private const int DonutPrice = 20;
    private const int FlashbangPrice = 8;

    public int Score;
    public int Lives;
    public int Credits;
    public RobustOfficerRank Rank;
    public int FireRate;
    public int Damage;
    public int Boots;
    public RobustOfficerPickup? Stocked;
    public int DonutsBought;

    public RobustOfficerRun(int lives)
    {
        Lives = lives;
    }

    public RobustOfficerWeapon Weapon => RobustOfficerWeapon.For(Rank);

    /// <summary>
    /// Hits the armor of the rank absorbs in each room.
    /// </summary>
    public int MaxArmor => (int) Rank;

    public float Speed => 4.5f + 0.6f * Boots;

    public float Cooldown => Weapon.Cooldown * MathF.Pow(0.85f, FireRate);

    public int BulletDamage => Weapon.Damage + Damage;

    /// <summary>
    /// A warden and the head of security get a turret at the start of each room.
    /// </summary>
    public bool HasTurret => Rank >= RobustOfficerRank.Warden;

    /// <summary>
    /// The head of security's shield gives back an armor point now and then.
    /// </summary>
    public bool RegeneratesArmor => Rank >= RobustOfficerRank.HeadOfSecurity;

    public RobustOfficerShopOffer Offer(RobustOfficerUpgrade upgrade)
    {
        var offer = new RobustOfficerShopOffer { Upgrade = upgrade };
        switch (upgrade)
        {
            case RobustOfficerUpgrade.Promotion:
                Fill(offer, (int) Rank, PromotionPrices);
                break;
            case RobustOfficerUpgrade.FireRate:
                Fill(offer, FireRate, FireRatePrices);
                break;
            case RobustOfficerUpgrade.Damage:
                Fill(offer, Damage, DamagePrices);
                break;
            case RobustOfficerUpgrade.Boots:
                Fill(offer, Boots, BootsPrices);
                break;
            case RobustOfficerUpgrade.Donut:
                offer.Level = Lives;
                offer.MaxLevel = MaxLives;
                offer.Price = Lives < MaxLives ? DonutPrice << Math.Min(DonutsBought, 20) : null;
                break;
            case RobustOfficerUpgrade.Flashbang:
                offer.MaxLevel = 1;
                offer.Level = Stocked == null ? 0 : 1;
                offer.Price = Stocked == null ? FlashbangPrice : null;
                break;
        }

        return offer;

        static void Fill(RobustOfficerShopOffer offer, int level, int[] prices)
        {
            offer.Level = level;
            offer.MaxLevel = prices.Length;
            offer.Price = level < prices.Length ? prices[level] : null;
        }
    }

    public List<RobustOfficerShopOffer> Offers()
    {
        return Enum.GetValues<RobustOfficerUpgrade>().Select(Offer).ToList();
    }

    public bool TryBuy(RobustOfficerUpgrade upgrade)
    {
        if (Offer(upgrade).Price is not { } price || price > Credits)
            return false;

        Credits -= price;
        switch (upgrade)
        {
            case RobustOfficerUpgrade.Promotion:
                Rank++;
                break;
            case RobustOfficerUpgrade.FireRate:
                FireRate++;
                break;
            case RobustOfficerUpgrade.Damage:
                Damage++;
                break;
            case RobustOfficerUpgrade.Boots:
                Boots++;
                break;
            case RobustOfficerUpgrade.Donut:
                Lives++;
                DonutsBought++;
                break;
            case RobustOfficerUpgrade.Flashbang:
                Stocked = RobustOfficerPickup.Flashbang;
                break;
        }

        return true;
    }
}

/// <summary>
/// A room ready to be played: its map and enemies, made harder at depth in the endless mode.
/// </summary>
public sealed class RobustOfficerRoom(
    IReadOnlyList<string> map,
    int seconds,
    RobustOfficerBoss? boss,
    List<RobustOfficerWave> waves,
    int theme = 0,
    float healthMultiplier = 1f,
    float spawnRate = 1f,
    float bossHealthMultiplier = 1f)
{
    public readonly IReadOnlyList<string> Map = map;
    public readonly int Seconds = seconds;
    public readonly RobustOfficerBoss? Boss = boss;
    public readonly List<RobustOfficerWave> Waves = waves;
    public readonly int Theme = theme;
    /// <summary>
    /// Multiplies the health of the enemies, rounded to the nearest; bosses only follow <see cref="BossHealthMultiplier"/>.
    /// </summary>
    public readonly float HealthMultiplier = healthMultiplier;

    /// <summary>
    /// Multiplies how often groups of enemies come in.
    /// </summary>
    public readonly float SpawnRate = spawnRate;

    /// <summary>
    /// The nth boss of a game has n times its health.
    /// </summary>
    public readonly float BossHealthMultiplier = bossHealthMultiplier;
}
