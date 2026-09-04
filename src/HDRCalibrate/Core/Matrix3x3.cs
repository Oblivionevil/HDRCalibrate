using System;

namespace HDRCalibrate.Core
{
    public sealed class Matrix3x3
    {
        private readonly double[,] _values;

        public Matrix3x3(double m11, double m12, double m13,
                         double m21, double m22, double m23,
                         double m31, double m32, double m33)
        {
            _values = new[,]
            {
                { m11, m12, m13 },
                { m21, m22, m23 },
                { m31, m32, m33 }
            };
        }

        public double this[int row, int column] => _values[row, column];

        public static Matrix3x3 Identity { get; } = new Matrix3x3(
            1, 0, 0,
            0, 1, 0,
            0, 0, 1);

        public Matrix3x3 Multiply(Matrix3x3 right)
        {
            var result = new double[3, 3];
            for (var row = 0; row < 3; row++)
            {
                for (var column = 0; column < 3; column++)
                {
                    for (var k = 0; k < 3; k++)
                    {
                        result[row, column] += this[row, k] * right[k, column];
                    }
                }
            }

            return FromArray(result);
        }

        public double[] Transform(double x, double y, double z)
        {
            return new[]
            {
                this[0, 0] * x + this[0, 1] * y + this[0, 2] * z,
                this[1, 0] * x + this[1, 1] * y + this[1, 2] * z,
                this[2, 0] * x + this[2, 1] * y + this[2, 2] * z
            };
        }

        public Matrix3x3 Inverse()
        {
            var a = this[0, 0]; var b = this[0, 1]; var c = this[0, 2];
            var d = this[1, 0]; var e = this[1, 1]; var f = this[1, 2];
            var g = this[2, 0]; var h = this[2, 1]; var i = this[2, 2];

            var determinant = a * (e * i - f * h)
                            - b * (d * i - f * g)
                            + c * (d * h - e * g);
            if (Math.Abs(determinant) < 1e-12)
            {
                throw new InvalidOperationException("Die Farbmatrix ist singulär.");
            }

            var inv = 1.0 / determinant;
            return new Matrix3x3(
                (e * i - f * h) * inv,
                (c * h - b * i) * inv,
                (b * f - c * e) * inv,
                (f * g - d * i) * inv,
                (a * i - c * g) * inv,
                (c * d - a * f) * inv,
                (d * h - e * g) * inv,
                (b * g - a * h) * inv,
                (a * e - b * d) * inv);
        }

        public static Matrix3x3 FromColumns(double[] first, double[] second, double[] third)
        {
            return new Matrix3x3(
                first[0], second[0], third[0],
                first[1], second[1], third[1],
                first[2], second[2], third[2]);
        }

        private static Matrix3x3 FromArray(double[,] values)
        {
            return new Matrix3x3(
                values[0, 0], values[0, 1], values[0, 2],
                values[1, 0], values[1, 1], values[1, 2],
                values[2, 0], values[2, 1], values[2, 2]);
        }
    }
}
