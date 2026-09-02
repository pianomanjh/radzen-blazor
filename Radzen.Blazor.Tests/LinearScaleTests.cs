using System;
using Xunit;

namespace Radzen.Blazor.Tests
{
    public class LinearScaleTests
    {
        [Fact]
        public void NiceNumber_WithNaN_ReturnsOne()
        {
            var scale = new LinearScale();

            Assert.Equal(1, scale.NiceNumber(double.NaN, false));
            Assert.Equal(1, scale.NiceNumber(double.NaN, true));
        }

        [Fact]
        public void NiceNumber_WithInfinity_ReturnsOne()
        {
            var scale = new LinearScale();

            Assert.Equal(1, scale.NiceNumber(double.PositiveInfinity, false));
            Assert.Equal(1, scale.NiceNumber(double.NegativeInfinity, true));
        }

        [Fact]
        public void Ticks_WithUnmeasuredScale_ReturnsFiniteRange()
        {
            // A scale which has neither received data nor been measured keeps the ScaleRange
            // defaults - an infinite input and output range. Dividing the infinite output size by
            // a zero tick distance used to produce NaN and throw from Math.Sign().
            var scale = new LinearScale();

            var (start, end, step) = scale.Ticks(0);

            Assert.True(double.IsFinite(start));
            Assert.True(double.IsFinite(end));
            Assert.True(double.IsFinite(step));
            Assert.True(step > 0);
        }

        [Fact]
        public void Ticks_WithZeroSizedOutput_ReturnsFiniteRange()
        {
            var scale = new LinearScale
            {
                Input = new ScaleRange { Start = 10, End = 100 },
                Output = new ScaleRange { Start = 0, End = 0 }
            };

            var (start, end, step) = scale.Ticks(0);

            Assert.True(double.IsFinite(start));
            Assert.True(double.IsFinite(end));
            Assert.True(double.IsFinite(step));
            Assert.True(step > 0);
        }

        [Fact]
        public void Ticks_WithMeasuredScale_IsUnaffected()
        {
            var scale = new LinearScale
            {
                Input = new ScaleRange { Start = 0, End = 90 },
                Output = new ScaleRange { Start = 0, End = 500 }
            };

            var (start, end, step) = scale.Ticks(100);

            Assert.Equal(0, start);
            Assert.Equal(100, end);
            Assert.Equal(20, step);
        }
    }
}
