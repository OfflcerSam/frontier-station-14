// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Numerics;
using Content.Server.Administration.Logs;
using Content.Server.Destructible;
using Content.Server._NF.Power.FuelModules;
using Content.Server.Weapons.Ranged.Systems;
using Content.Shared.Damage;
using Content.Shared.Database;
using Content.Shared.FixedPoint;
using Content.Shared.Power.Generator;
using Content.Shared.Projectiles;
using Robust.Shared.Random;

namespace Content.Server._NF.Power.Generator;

/// <summary>Mechanical breakup uses real projectiles, never pressure damage or knockdown.</summary>
public sealed class CombustionShrapnelSystem : EntitySystem
{
    [Dependency] private readonly DestructibleSystem _destructible = default!;
    [Dependency] private readonly GunSystem _guns = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IAdminLogManager _logs = default!;
    [Dependency] private readonly FuelModuleSystem _modules = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<CombustionShrapnelComponent, DamageChangedEvent>(OnDamage,
            before: new[] { typeof(DestructibleSystem) }, after: new[] { typeof(GeneratorFireSystem) });
        SubscribeLocalEvent<GeneratorShrapnelComponent, ProjectileHitEvent>(OnHit);
    }

    private void OnDamage(Entity<CombustionShrapnelComponent> ent, ref DamageChangedEvent args)
    {
        if (!args.DamageIncreased || args.DamageDelta == null || args.DamageDelta.GetTotal() <= FixedPoint2.Zero ||
            !_destructible.TryGetDestroyedAt(ent.Owner, out var threshold) || threshold <= FixedPoint2.Zero)
            return;
        var destroyed = args.Damageable.TotalDamage >= threshold.Value;
        if (destroyed)
        {
            if (ent.Comp.DestructionBurstUsed)
                return;
            ent.Comp.DestructionBurstUsed = true;
            Release(ent, ent.Comp.DestructionFragments, args.Origin);
            if (_modules.GetInstalledModule(ent) is { } module)
                _modules.SpillContents((module, Comp<Content.Shared._NF.Power.FuelModules.FuelModuleComponent>(module)), 1f);
            if (TryComp<Content.Shared.Atmos.Components.FlammableComponent>(ent, out var flame) && flame.OnFire)
                EntityManager.System<FuelPuddleFireSystem>().IgniteAt(ent.Owner);
            return;
        }
        if (!ent.Comp.HitBurstUsed && TryComp<FuelGeneratorComponent>(ent, out var generator) && generator.On &&
            args.DamageDelta.GetTotal().Float() >= threshold.Value.Float() * ent.Comp.CatastrophicHitFraction)
        {
            ent.Comp.HitBurstUsed = true;
            Release(ent, ent.Comp.HitFragments, args.Origin);
        }
    }

    private void Release(Entity<CombustionShrapnelComponent> ent, int count, EntityUid? actor)
    {
        var xform = Transform(ent);
        var coordinates = xform.Coordinates.Offset(xform.LocalRotation.RotateVec(ent.Comp.ReleaseOffset));
        var map = _transform.ToMapCoordinates(coordinates);
        var angle = _random.NextAngle().Theta;
        for (var i = 0; i < count; i++)
        {
            var projectile = Spawn("NFGeneratorShrapnel", map);
            var range = Comp<GeneratorShrapnelComponent>(projectile);
            range.Origin = map.Position;
            range.Launched = true;
            var radians = angle + 2 * Math.PI * i / count;
            _guns.ShootProjectile(projectile, new Vector2((float) Math.Cos(radians), (float) Math.Sin(radians)),
                Vector2.Zero, ent.Owner, ent.Owner, 20f);
        }
        _logs.Add(LogType.Damaged, LogImpact.High,
            $"{ToPrettyString(ent.Owner):subject} released {count} mechanical fragments; damage source {ToPrettyString(actor):actor}.");
    }

    private void OnHit(Entity<GeneratorShrapnelComponent> ent, ref ProjectileHitEvent args)
    {
        if (ent.Comp.Launched && Vector2.Distance(_transform.GetWorldPosition(ent), ent.Comp.Origin) > ent.Comp.MaximumRange)
            args.Damage = new DamageSpecifier();
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<GeneratorShrapnelComponent>();
        while (query.MoveNext(out var uid, out var range))
            if (range.Launched && Vector2.Distance(_transform.GetWorldPosition(uid), range.Origin) >= range.MaximumRange)
                QueueDel(uid);
    }
}
