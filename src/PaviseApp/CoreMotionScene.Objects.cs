using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace PaviseApp
{
    internal sealed partial class CoreMotionScene
    {
        private void BinaryStars(double t)
        {
            double turn = -.38, phase = t * .48;
            Halo(0, 0, 175, accent, .22);
            for (int tail = 0; tail < 2; tail++)
            {
                PointF[] trail = new PointF[80];
                for (int i = 0; i < trail.Length; i++)
                {
                    double a = phase + tail * Math.PI - (1 - i / 79.0) * 4.3;
                    trail[i] = DiskPoint(a, 90 + Math.Sin(a * 2 - t * .3) * 13, .54, turn);
                }
                Ribbon(trail, A(tail == 0 ? accent : high, 95), 3.2f);
                for (int lane = 0; lane < 3; lane++)
                {
                    PointF[] strand = new PointF[55];
                    for (int i = 0; i < strand.Length; i++)
                    {
                        double a = phase + tail * Math.PI - (1 - i / 54.0) * 3.2;
                        strand[i] = DiskPoint(a, 92 + lane * 6 + Math.Sin(a * 3 + t) * 6, .54, turn);
                    }
                    Ribbon(strand, A(second, 50 + lane * 15), .7f);
                }
            }
            for (int pass = 0; pass < 2; pass++)
            for (int body = 0; body < 2; body++)
            {
                double a = phase + body * Math.PI;
                if ((Math.Sin(a) >= 0) != (pass == 1)) continue;
                PointF at = DiskPoint(a, 96, .54, turn);
                Orb(at.X, at.Y, body == 0 ? 29 : 20, body == 0 ? accent : second, 1);
            }
            for (int i = 0; i < 42; i++)
            {
                double q = Fract(i * .618 + t * .16), a = i * 2.399963 - q * 1.8 + phase;
                PointF at = DiskPoint(a, 174 - q * 120, .72, turn);
                Dot(at.X, at.Y, .8 + q * .6, A(high, Math.Sin(q * Math.PI) * 130));
            }
        }
        private void PhotonWeave(double t)
        {
            double yaw = t * .29, tilt = -.35 + Math.Sin(t * .39) * .54;
            double edge = 69 + Math.Sin(t * .83) * 12;
            Halo(0, 0, 180, accent, .35);
            PointF[] cube = new PointF[8];
            for (int i = 0; i < 8; i++) cube[i] = SpacePoint((i & 1) == 0 ? -edge : edge,
                (i & 2) == 0 ? -edge : edge, (i & 4) == 0 ? -edge : edge, yaw, tilt);
            int[][] faces = { new[] { 0, 1, 3, 2 }, new[] { 4, 6, 7, 5 }, new[] { 0, 4, 5, 1 },
                new[] { 2, 3, 7, 6 }, new[] { 0, 2, 6, 4 }, new[] { 1, 5, 7, 3 } };
            foreach (int[] face in faces)
            {
                PointF[] panel = new PointF[4];
                for (int i = 0; i < 4; i++) panel[i] = cube[face[i]];
                Surface(panel, A(accent, light ? 25 : 32), A(second, 8));
            }
            for (int i = 0; i < 8; i++)
            {
                for (int axis = 1; axis <= 4; axis *= 2)
                {
                    if ((i & axis) != 0) continue;
                    PointF end = cube[i | axis];
                    Beam(A(second, 175), 1.25f, cube[i], end);
                    double q = Fract(t * .26 + i * .137 + axis * .17);
                    PointF point = P(cube[i].X + (end.X - cube[i].X) * q, cube[i].Y + (end.Y - cube[i].Y) * q);
                    Halo(point.X, point.Y, 8, high, Math.Sin(q * Math.PI) * .9);
                    Dot(point.X, point.Y, 1.8, A(high, Math.Sin(q * Math.PI) * 240));
                }
                Orb(cube[i].X, cube[i].Y, 3.2, second, .4);
            }
            GraphicsState inner = g.Save(); g.RotateTransform((float)(-t * 23 % 360));
            double size = 25 + Math.Sin(t * 1.4) * 4;
            Surface(new[] { P(0, -size), P(size * .7, 0), P(0, size), P(-size * .7, 0) }, A(high, 240), A(accent, 150));
            g.Restore(inner);
            for (int i = 0; i < 4; i++)
            {
                PointF at = Polar(157, t * .12 + i * Math.PI / 2);
                Line(A(accent, 55), 1, P(at.X - 5, at.Y), P(at.X + 5, at.Y));
                Line(A(accent, 55), 1, P(at.X, at.Y - 5), P(at.X, at.Y + 5));
            }
        }
        private void NovaShock(double t)
        {
            double breath = .5 + .5 * Math.Sin(t * 1.18), core = 24 + breath * 10;
            Color stellarInk = light ? Col.Lerp(accent, Color.Black, .22f) : second;
            Halo(0, 0, 170, accent, .28 + breath * .34);
            for (int ring = 0; ring < 3; ring++)
            {
                double q = Fract(t * .19 + ring / 3.0), r = 42 + q * 140;
                PointF[] wave = new PointF[100];
                for (int i = 0; i < wave.Length; i++)
                {
                    double a = i * Tau / 99;
                    wave[i] = Polar(r + Math.Sin(a * 8 + t * .7) * 3 * q, a);
                }
                Ribbon(wave, A(stellarInk, Math.Sin(q * Math.PI) * (1 - q) * (light ? 210 : 150)), 1.5f);
            }
            for (int ray = 0; ray < 12; ray++)
            {
                double a = ray * Tau / 12 + t * .06;
                double length = 68 + 54 * (.5 + .5 * Math.Sin(t * 1.18 - ray * .37));
                Surface(new[] { Polar(core * .8, a - .17), Polar(length, a), Polar(core * .8, a + .17), Polar(core * .5, a) },
                    A(stellarInk, ray % 3 == 0 ? 200 : (light ? 140 : 105)), A(accent, 20));
            }
            Orb(0, 0, core, accent, 1);
            Ribbon(new[] { P(-65 - breath * 25, 0), P(0, -1), P(65 + breath * 25, 0) }, A(high, 210), 1);
            for (int i = 0; i < 50; i++)
            {
                double q = Fract(t * .23 + i * .618), a = i * 2.399963;
                Line(A(high, Math.Sin(q * Math.PI) * 150), .9f, Polar(37 + q * 150, a), Polar(40 + q * 150, a));
            }
        }
        private void OrbitalStructure(double t)
        {
            Halo(0, 0, 185, accent, .22);
            // 环带按前后半圈分层，星体不会穿透中心球。
            for (int pass = 0; pass < 2; pass++)
            {
                if (pass == 1) Orb(0, 0, 40 + Math.Sin(t * .9) * 2, accent, 1);
                for (int orbit = 0; orbit < 3; orbit++)
                {
                    double turn = orbit * 1.03 + .18 * Math.Sin(t * .22 + orbit), radius = 121 + orbit * 13;
                    PointF[] path = new PointF[61];
                    for (int i = 0; i < path.Length; i++)
                        path[i] = DiskPoint((pass == 0 ? Math.PI : 0) + i * Math.PI / 60, radius, .33 + orbit * .08, turn);
                    Ribbon(path, A(orbit == 1 ? high : second, pass == 0 ? 70 : 160), orbit == 1 ? 1.6f : 1.1f);
                    double a = t * (.37 - orbit * .07) + orbit * 2.1;
                    if ((Math.Sin(a) > 0) != (pass == 1)) continue;
                    PointF at = DiskPoint(a, radius, .33 + orbit * .08, turn);
                    Orb(at.X, at.Y, 7 + orbit * 2, second, .65);
                }
            }
            for (int i = 0; i < 54; i++)
            {
                double a = i * Tau / 54 - t * .06;
                Line(A(accent, i % 3 == 0 ? 105 : 35), .8f, Polar(177, a), Polar(i % 3 == 0 ? 182 : 179, a));
            }
        }
        private void TidalField(double t)
        {
            double lean = Math.Sin(t * .35) * .19;
            GraphicsState glass = g.Save(); g.RotateTransform((float)(lean * 180 / Math.PI));
            Halo(0, 0, 180, accent, .23);
            for (int side = -1; side <= 1; side += 2)
            {
                Surface(new[] { P(-76, side * 113), P(76, side * 113), P(65, side * 81), P(18, side * 27),
                    P(7, side * 8), P(-7, side * 8), P(-18, side * 27), P(-65, side * 81) }, A(second, 40), A(accent, 10));
                Ribbon(new[] { P(-73, side * 109), P(-60, side * 78), P(-25, side * 38), P(-6, side * 10) }, A(second, 165), 1.5f);
                Ribbon(new[] { P(73, side * 109), P(60, side * 78), P(25, side * 38), P(6, side * 10) }, A(high, 185), 1.3f);
                double level = 65 + side * Math.Sin(t * .65) * 20;
                double sandWidth = 12 + (level - 27) * 47 / 54;
                Surface(new[] { P(-sandWidth, side * level), P(sandWidth, side * level), P(66, side * 104), P(-66, side * 104) }, A(accent, 90), A(second, 170));
                Surface(new[] { P(-90, side * 117), P(90, side * 117), P(101, side * 126), P(76, side * 136),
                    P(-76, side * 136), P(-101, side * 126) }, A(second, 180), A(dark, 235));
                Beam(A(high, 160), 1, P(-84, side * 120), P(84, side * 120));
            }
            Ribbon(new[] { P(0, -8), P(-1, 0), P(1, 26), P(0, 76) }, A(high, 170), 1.8f);
            for (int i = 0; i < 80; i++)
            {
                double q = Fract(t * .32 + i * .618), y = -88 + q * 180;
                double spread = Math.Abs(y) * .37;
                double x = Math.Sin(i * 17.3) * spread * Math.Pow(Math.Abs(y) / 90, .5);
                Dot(x, y, .8 + i % 3 * .22, A(i % 4 == 0 ? high : second, Math.Sin(q * Math.PI) * 195));
            }
            for (int i = 0; i < 8; i++)
            {
                PointF at = DiskPoint(t * .27 + i * Tau / 8, 146, .82, 0);
                Dot(at.X, at.Y, 1.1, A(second, 65));
            }
            g.Restore(glass);
        }
    }
}
