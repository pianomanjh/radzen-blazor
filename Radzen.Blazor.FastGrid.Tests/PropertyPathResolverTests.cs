using System;
using System.Linq.Expressions;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    // The path is what LoadDataArgs.OrderBy, OData $orderby, settings persistence and
    // FilterDescriptor.Property all consume, so getting it wrong is silent: the grid still renders.
    public class PropertyPathResolverTests
    {
        [Fact]
        public void SimpleMember_ResolvesToPropertyName()
        {
            Assert.Equal("Id", PropertyPathResolver.For<Person, int>(p => p.Id));
            Assert.Equal("First", PropertyPathResolver.For<Person, string>(p => p.First));
            Assert.Equal("Hired", PropertyPathResolver.For<Person, DateTime>(p => p.Hired));
        }

        [Fact]
        public void NestedMember_ResolvesToDottedPath()
        {
            Assert.Equal("Customer.Name", PropertyPathResolver.For<Person, string>(p => p.Customer.Name));
        }

        [Fact]
        public void BoxedMember_StripsTheConvertAndResolvesTheSamePath()
        {
            // Expression<Func<T, object>> wraps a value type in a Convert node. The boxed authoring style
            // has to land on the same path as the typed one, or a grid authored either way keys its
            // persisted settings differently.
            Assert.Equal("Id", PropertyPathResolver.For<Person, object>(p => (object)p.Id));
            Assert.Equal("Hired", PropertyPathResolver.For<Person, object>(p => (object)p.Hired));
            Assert.Equal("Customer.Name", PropertyPathResolver.For<Person, object>(p => p.Customer.Name));
        }

        [Fact]
        public void CheckedConvert_IsAlsoStripped()
        {
            var parameter = Expression.Parameter(typeof(Person), "p");
            var body = Expression.ConvertChecked(Expression.Property(parameter, nameof(Person.Id)), typeof(long));
            var expression = Expression.Lambda<Func<Person, long>>(body, parameter);

            Assert.Equal("Id", PropertyPathResolver.For(expression));
        }

        [Fact]
        public void ComputedExpression_HasNoPath()
        {
            // A computed column renders fine but cannot sort server side, round-trip through LoadData or
            // persist. Returning null is what makes that visible at the call site.
            Assert.Null(PropertyPathResolver.For<Person, int>(p => p.Id + p.Id));
            Assert.Null(PropertyPathResolver.For<Person, string>(p => p.First + " " + p.Last));
            Assert.Null(PropertyPathResolver.For<Person, string>(p => p.First.ToUpperInvariant()));
            Assert.Null(PropertyPathResolver.For<Person, int>(p => 42));
            Assert.Null(PropertyPathResolver.For<Person, string>(p => p.First.Length > 3 ? p.First : p.Last));
            Assert.Null(PropertyPathResolver.For<Person, Person>(p => p));
        }

        [Fact]
        public void MemberOfAComputedSubexpression_HasNoPath()
        {
            // p.Customer.Name is a path; (p.Customer ?? other).Name is not. The walk has to reach the
            // lambda parameter, not merely find some member access on the way down.
            Assert.Null(PropertyPathResolver.For<Person, string>(p => (p.Customer ?? new Company()).Name));
            Assert.Null(PropertyPathResolver.For<Person, int>(p => p.First.ToUpperInvariant().Length));
        }

        [Fact]
        public void ClosureCapture_HasNoPath()
        {
            // A captured local reads as a member access on a compiler-generated closure class. Its
            // "expression" is a ConstantExpression, not the lambda parameter, so it must not resolve.
            var captured = new Company { Name = "Acme" };

            Assert.Null(PropertyPathResolver.For<Person, string>(p => captured.Name));
        }

        [Fact]
        public void NullExpression_HasNoPath()
        {
            Assert.Null(PropertyPathResolver.For<Person, string>(null));
        }
    }
}
