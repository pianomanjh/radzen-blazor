using Bunit;
using Xunit;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Radzen.Blazor.Tests
{
    public class TreeTests
    {
        class Category
        {
            public string Name { get; set; }
            public List<Product> Products { get; set; } = new List<Product>();
        }

        class Product
        {
            public string Name { get; set; }
        }

        class Employee
        {
            public string FirstName { get; set; }
            public string LastName { get; set; }
            public List<Employee> Employees { get; set; } = new List<Employee>();
        }

        [Fact]
        public void Tree_Renders_WithClassName()
        {
            using var ctx = new TestContext();
            var component = ctx.RenderComponent<RadzenTree>();

            Assert.Contains(@"rz-tree", component.Markup);
        }

        [Fact]
        public void Tree_Renders_TreeContainer()
        {
            using var ctx = new TestContext();
            var component = ctx.RenderComponent<RadzenTree>();

            Assert.Contains("rz-tree-container", component.Markup);
        }

        [Fact]
        public void Tree_Renders_TabIndex()
        {
            using var ctx = new TestContext();
            var component = ctx.RenderComponent<RadzenTree>();

            Assert.Contains("tabindex=\"0\"", component.Markup);
        }

        [Fact]
        public void Tree_Renders_WithData_SingleLevel()
        {
            using var ctx = new TestContext();
            var data = new List<Category>
            {
                new Category { Name = "Electronics" },
                new Category { Name = "Clothing" }
            };

            var component = ctx.RenderComponent<RadzenTree>(parameters =>
            {
                parameters.Add(p => p.Data, data);
                parameters.Add(p => p.ChildContent, builder =>
                {
                    builder.OpenComponent<RadzenTreeLevel>(0);
                    builder.AddAttribute(1, "TextProperty", "Name");
                    builder.CloseComponent();
                });
            });

            Assert.Contains("Electronics", component.Markup);
            Assert.Contains("Clothing", component.Markup);
        }

        [Fact]
        public void Tree_Renders_WithData_HierarchicalData()
        {
            using var ctx = new TestContext();
            var data = new List<Category>
            {
                new Category 
                { 
                    Name = "Electronics",
                    Products = new List<Product>
                    {
                        new Product { Name = "Laptop" },
                        new Product { Name = "Phone" }
                    }
                }
            };

            var component = ctx.RenderComponent<RadzenTree>(parameters =>
            {
                parameters.Add(p => p.Data, data);
                parameters.Add(p => p.ChildContent, builder =>
                {
                    builder.OpenComponent<RadzenTreeLevel>(0);
                    builder.AddAttribute(1, "TextProperty", "Name");
                    builder.AddAttribute(2, "ChildrenProperty", "Products");
                    builder.CloseComponent();

                    builder.OpenComponent<RadzenTreeLevel>(3);
                    builder.AddAttribute(4, "TextProperty", "Name");
                    builder.AddAttribute(5, "HasChildren", (object product) => false);
                    builder.CloseComponent();
                });
            });

            Assert.Contains("Electronics", component.Markup);
        }

        [Fact]
        public void Tree_Renders_WithData_SelfReferencing()
        {
            using var ctx = new TestContext();
            var data = new List<Employee>
            {
                new Employee 
                { 
                    FirstName = "Nancy", 
                    LastName = "Davolio",
                    Employees = new List<Employee>
                    {
                        new Employee { FirstName = "Andrew", LastName = "Fuller" }
                    }
                }
            };

            var component = ctx.RenderComponent<RadzenTree>(parameters =>
            {
                parameters.Add(p => p.Data, data);
                parameters.Add(p => p.ChildContent, builder =>
                {
                    builder.OpenComponent<RadzenTreeLevel>(0);
                    builder.AddAttribute(1, "TextProperty", "LastName");
                    builder.AddAttribute(2, "ChildrenProperty", "Employees");
                    builder.AddAttribute(3, "HasChildren", (object e) => (e as Employee).Employees.Any());
                    builder.CloseComponent();
                });
            });

            Assert.Contains("Davolio", component.Markup);
        }

        [Fact]
        public void Tree_Renders_WithCheckBoxes()
        {
            using var ctx = new TestContext();
            var data = new List<Category>
            {
                new Category { Name = "Electronics" }
            };

            var component = ctx.RenderComponent<RadzenTree>(parameters =>
            {
                parameters.Add(p => p.AllowCheckBoxes, true);
                parameters.Add(p => p.Data, data);
                parameters.Add(p => p.ChildContent, builder =>
                {
                    builder.OpenComponent<RadzenTreeLevel>(0);
                    builder.AddAttribute(1, "TextProperty", "Name");
                    builder.CloseComponent();
                });
            });

            Assert.Contains("rz-chkbox", component.Markup);
        }

        [Fact]
        public void Tree_CheckParents_RendersMixed_AndReflectsInPlaceMutation()
        {
            using var ctx = new TestContext();
            var laptop = new Product { Name = "Laptop" };
            var phone = new Product { Name = "Phone" };
            var data = new List<Category>
            {
                new Category { Name = "Electronics", Products = new List<Product> { laptop, phone } }
            };

            // One of the two children checked -> the parent must render the mixed tri-state.
            var checkedValues = new List<object> { laptop };

            void BuildTree(ComponentParameterCollectionBuilder<RadzenTree> parameters)
            {
                parameters.Add(p => p.AllowCheckBoxes, true);
                parameters.Add(p => p.AllowCheckParents, true);
                parameters.Add(p => p.CheckedValues, checkedValues);
                parameters.Add(p => p.Data, data);
                parameters.Add(p => p.ChildContent, builder =>
                {
                    builder.OpenComponent<RadzenTreeLevel>(0);
                    builder.AddAttribute(1, "TextProperty", "Name");
                    builder.AddAttribute(2, "ChildrenProperty", "Products");
                    builder.AddAttribute(3, "Expanded", (object c) => true);
                    builder.AddAttribute(4, "HasChildren", (object c) => c is Category);
                    builder.CloseComponent();

                    builder.OpenComponent<RadzenTreeLevel>(5);
                    builder.AddAttribute(6, "TextProperty", "Name");
                    builder.AddAttribute(7, "HasChildren", (object product) => false);
                    builder.CloseComponent();
                });
            }

            var component = ctx.RenderComponent<RadzenTree>(BuildTree);

            // The parent renders its aria-checked before its child items register, so the tri-state settles
            // on the following render pass. Render once more to reach the settled state.
            component.Render();

            var parent = component.FindAll("[role=treeitem]").First(i => i.GetAttribute("aria-level") == "1");
            Assert.Equal("mixed", parent.GetAttribute("aria-checked"));

            // Check the second child in place (same list reference). The memoized checked-value set must
            // refresh on re-render, so the parent is no longer mixed.
            checkedValues.Add(phone);
            component.SetParametersAndRender(BuildTree);

            parent = component.FindAll("[role=treeitem]").First(i => i.GetAttribute("aria-level") == "1");
            Assert.Equal("false", parent.GetAttribute("aria-checked"));
        }

        // The count-changing edit above is the easy half. A same-count edit - one checked value swapped
        // for another - changes neither the list reference nor its count, and a render the tree raises
        // itself never passes through OnParametersSet. Nothing about the memoized set can be inferred
        // from the outside, so it has to be discarded whenever the tree renders.
        //
        // A flat tree, deliberately: expanding a node routes CheckedValues through SetCheckedValues,
        // which copies it, and the tree then holds a list the caller can no longer mutate at all.
        [Fact]
        public async Task Tree_ReflectsSameCountMutation_OnInternalRender()
        {
            using var ctx = new TestContext();
            var laptop = new Product { Name = "Laptop" };
            var phone = new Product { Name = "Phone" };
            var keyboard = new Product { Name = "Keyboard" };
            var checkedValues = new List<object> { laptop, phone };

            var component = ctx.RenderComponent<RadzenTree>(parameters =>
            {
                parameters.Add(p => p.AllowCheckBoxes, true);
                parameters.Add(p => p.CheckedValues, checkedValues);
                parameters.Add(p => p.Data, new List<Product> { laptop, phone, keyboard });
                parameters.Add(p => p.ChildContent, builder =>
                {
                    builder.OpenComponent<RadzenTreeLevel>(0);
                    builder.AddAttribute(1, "TextProperty", "Name");
                    builder.AddAttribute(2, "HasChildren", (object product) => false);
                    builder.CloseComponent();
                });
            });

            string Checked(string text) => component.FindAll("[role=treeitem]")
                .First(i => i.TextContent.Contains(text)).GetAttribute("aria-checked");

            Assert.Equal("true", Checked("Laptop"));
            Assert.Equal("false", Checked("Keyboard"));

            // Swap Laptop out for Keyboard: same list, same count, nothing observable changed about it.
            // The tree raises the render itself, as it does when a caller edits the bound collection
            // from a handler.
            checkedValues[0] = keyboard;
            await component.InvokeAsync(() => component.Instance.ChangeState());

            Assert.Equal("false", Checked("Laptop"));
            Assert.Equal("true", Checked("Keyboard"));
        }

        // A HashSet built with an IEqualityComparer answers membership its own way, and CheckedValues is
        // IEnumerable<object>, so such a set arrives as ICollection<object> - which is exactly what
        // Cast().Contains() dispatched to before the memo. Rebuilding it as a HashSet with the default
        // comparer asks a different question and gets a different answer.
        sealed class ByName : IEqualityComparer<object>
        {
            public new bool Equals(object x, object y) => Name(x) == Name(y);

            public int GetHashCode(object obj) => Name(obj)?.GetHashCode() ?? 0;

            static string Name(object o) => (o as Product)?.Name;
        }

        [Fact]
        public void Tree_HonoursTheComparerOnTheBoundCheckedValues()
        {
            using var ctx = new TestContext();

            var laptop = new Product { Name = "Laptop" };
            var keyboard = new Product { Name = "Keyboard" };

            // A different instance carrying the same name. Under the set's own comparer the tree's Laptop
            // is checked; under reference equality it is not.
            var checkedValues = new HashSet<object>(new ByName()) { new Product { Name = "Laptop" } };

            var component = ctx.RenderComponent<RadzenTree>(parameters =>
            {
                parameters.Add(p => p.AllowCheckBoxes, true);
                parameters.Add(p => p.CheckedValues, checkedValues);
                parameters.Add(p => p.Data, new List<Product> { laptop, keyboard });
                parameters.Add(p => p.ChildContent, builder =>
                {
                    builder.OpenComponent<RadzenTreeLevel>(0);
                    builder.AddAttribute(1, "TextProperty", "Name");
                    builder.AddAttribute(2, "HasChildren", (object product) => false);
                    builder.CloseComponent();
                });
            });

            string Checked(string text) => component.FindAll("[role=treeitem]")
                .First(i => i.TextContent.Contains(text)).GetAttribute("aria-checked");

            Assert.Equal("true", Checked("Laptop"));
            Assert.Equal("false", Checked("Keyboard"));
        }

        // ShouldRender on the tree only runs when the tree renders. RadzenTreeItem renders independently -
        // after its own click, or its own StateHasChanged - and reads the memo through IsChecked without
        // the tree's lifecycle running at all, so a memo discarded only on the tree's renders survived
        // into an item-initiated one and answered from state that had already changed.
        [Fact]
        public void Tree_ReflectsSameCountMutation_OnItemOnlyRender()
        {
            using var ctx = new TestContext();

            var laptop = new Product { Name = "Laptop" };
            var phone = new Product { Name = "Phone" };
            var keyboard = new Product { Name = "Keyboard" };
            var checkedValues = new List<object> { laptop, phone };

            var component = ctx.RenderComponent<RadzenTree>(parameters =>
            {
                parameters.Add(p => p.AllowCheckBoxes, true);
                parameters.Add(p => p.CheckedValues, checkedValues);
                parameters.Add(p => p.Data, new List<Product> { laptop, phone, keyboard });
                parameters.Add(p => p.ChildContent, builder =>
                {
                    builder.OpenComponent<RadzenTreeLevel>(0);
                    builder.AddAttribute(1, "TextProperty", "Name");
                    builder.AddAttribute(2, "HasChildren", (object product) => false);
                    builder.CloseComponent();
                });
            });

            string Checked(string text) => component.FindAll("[role=treeitem]")
                .First(i => i.TextContent.Contains(text)).GetAttribute("aria-checked");

            Assert.Equal("true", Checked("Laptop"));

            // Same list, same count, nothing observable changed about it - and then a render that only
            // the item raises, with the tree's own lifecycle untouched.
            checkedValues[0] = keyboard;

            component.FindAll("[role=treeitem]").First(i => i.TextContent.Contains("Laptop")).Click();

            // Laptop only. Keyboard's item did not render in that batch, so its markup is still the
            // markup of the previous one - which is Blazor working correctly, not the memo.
            Assert.Equal("false", Checked("Laptop"));
        }

        [Fact]
        public void Tree_Renders_WithExpandableItems()
        {
            using var ctx = new TestContext();
            var data = new List<Category>
            {
                new Category 
                { 
                    Name = "Electronics",
                    Products = new List<Product>
                    {
                        new Product { Name = "Laptop" }
                    }
                }
            };

            var component = ctx.RenderComponent<RadzenTree>(parameters =>
            {
                parameters.Add(p => p.Data, data);
                parameters.Add(p => p.ChildContent, builder =>
                {
                    builder.OpenComponent<RadzenTreeLevel>(0);
                    builder.AddAttribute(1, "TextProperty", "Name");
                    builder.AddAttribute(2, "ChildrenProperty", "Products");
                    builder.CloseComponent();

                    builder.OpenComponent<RadzenTreeLevel>(3);
                    builder.AddAttribute(4, "TextProperty", "Name");
                    builder.CloseComponent();
                });
            });

            // Expandable items should have a toggle icon
            Assert.Contains("rz-tree-toggler", component.Markup);
        }

        [Fact]
        public void Tree_Renders_TreeRole()
        {
            using var ctx = new TestContext();
            var component = ctx.RenderComponent<RadzenTree>();

            var container = component.Find("[role=tree]");

            Assert.Equal("0", container.GetAttribute("tabindex"));
        }

        [Fact]
        public void Tree_Renders_AriaLabel()
        {
            using var ctx = new TestContext();
            var component = ctx.RenderComponent<RadzenTree>(parameters =>
            {
                parameters.Add(p => p.AriaLabel, "Categories");
            });

            var container = component.Find("[role=tree]");

            Assert.Equal("Categories", container.GetAttribute("aria-label"));
        }

        [Fact]
        public void Tree_Renders_AriaLabelledBy()
        {
            using var ctx = new TestContext();
            var component = ctx.RenderComponent<RadzenTree>(parameters =>
            {
                parameters.Add(p => p.AriaLabelledBy, "tree-heading");
            });

            var container = component.Find("[role=tree]");

            Assert.Equal("tree-heading", container.GetAttribute("aria-labelledby"));
        }

        [Fact]
        public void Tree_Renders_SetSizeAndPosInSet()
        {
            using var ctx = new TestContext();
            var data = new List<Category>
            {
                new Category
                {
                    Name = "Electronics",
                    Products = new List<Product>
                    {
                        new Product { Name = "Laptop" },
                        new Product { Name = "Phone" },
                        new Product { Name = "Tablet" }
                    }
                },
                new Category
                {
                    Name = "Books",
                    Products = new List<Product>()
                }
            };

            var component = ctx.RenderComponent<RadzenTree>(parameters =>
            {
                parameters.Add(p => p.Data, data);
                parameters.Add(p => p.ChildContent, builder =>
                {
                    builder.OpenComponent<RadzenTreeLevel>(0);
                    builder.AddAttribute(1, "TextProperty", "Name");
                    builder.AddAttribute(2, "ChildrenProperty", "Products");
                    builder.AddAttribute(3, "Expanded", (object c) => true);
                    builder.AddAttribute(4, "HasChildren", (object c) => c is Category);
                    builder.CloseComponent();

                    builder.OpenComponent<RadzenTreeLevel>(5);
                    builder.AddAttribute(6, "TextProperty", "Name");
                    builder.AddAttribute(7, "HasChildren", (object product) => false);
                    builder.CloseComponent();
                });
            });

            var treeItems = component.FindAll("[role=treeitem]");

            var roots = treeItems.Where(i => i.GetAttribute("aria-level") == "1").ToList();

            Assert.Equal(2, roots.Count);

            Assert.Equal("2", roots[0].GetAttribute("aria-setsize"));
            Assert.Equal("1", roots[0].GetAttribute("aria-posinset"));
            Assert.Equal("2", roots[1].GetAttribute("aria-setsize"));
            Assert.Equal("2", roots[1].GetAttribute("aria-posinset"));

            var children = treeItems.Where(i => i.GetAttribute("aria-level") == "2").ToList();

            Assert.Equal(3, children.Count);

            for (var i = 0; i < children.Count; i++)
            {
                Assert.Equal("3", children[i].GetAttribute("aria-setsize"));
                Assert.Equal((i + 1).ToString(), children[i].GetAttribute("aria-posinset"));
            }
        }

        [Fact]
        public void Tree_Renders_TreeItemRoleAndLevel()
        {
            using var ctx = new TestContext();
            var data = new List<Category>
            {
                new Category
                {
                    Name = "Electronics",
                    Products = new List<Product>
                    {
                        new Product { Name = "Laptop" }
                    }
                }
            };

            var component = ctx.RenderComponent<RadzenTree>(parameters =>
            {
                parameters.Add(p => p.Data, data);
                parameters.Add(p => p.ChildContent, builder =>
                {
                    builder.OpenComponent<RadzenTreeLevel>(0);
                    builder.AddAttribute(1, "TextProperty", "Name");
                    builder.AddAttribute(2, "ChildrenProperty", "Products");
                    builder.AddAttribute(3, "Expanded", (object c) => true);
                    builder.AddAttribute(4, "HasChildren", (object c) => c is Category);
                    builder.CloseComponent();

                    builder.OpenComponent<RadzenTreeLevel>(5);
                    builder.AddAttribute(6, "TextProperty", "Name");
                    builder.AddAttribute(7, "HasChildren", (object product) => false);
                    builder.CloseComponent();
                });
            });

            var treeItems = component.FindAll("[role=treeitem]");

            Assert.NotEmpty(treeItems);

            var root = treeItems.First();
            Assert.Equal("1", root.GetAttribute("aria-level"));
            Assert.Equal("true", root.GetAttribute("aria-expanded"));

            var child = treeItems.Last();
            Assert.Equal("2", child.GetAttribute("aria-level"));
        }

        [Fact]
        public void Tree_Renders_GroupRoleOnSubtree()
        {
            using var ctx = new TestContext();
            var data = new List<Category>
            {
                new Category
                {
                    Name = "Electronics",
                    Products = new List<Product>
                    {
                        new Product { Name = "Laptop" }
                    }
                }
            };

            var component = ctx.RenderComponent<RadzenTree>(parameters =>
            {
                parameters.Add(p => p.Data, data);
                parameters.Add(p => p.ChildContent, builder =>
                {
                    builder.OpenComponent<RadzenTreeLevel>(0);
                    builder.AddAttribute(1, "TextProperty", "Name");
                    builder.AddAttribute(2, "ChildrenProperty", "Products");
                    builder.AddAttribute(3, "Expanded", (object c) => true);
                    builder.AddAttribute(4, "HasChildren", (object c) => c is Category);
                    builder.CloseComponent();

                    builder.OpenComponent<RadzenTreeLevel>(5);
                    builder.AddAttribute(6, "TextProperty", "Name");
                    builder.AddAttribute(7, "HasChildren", (object product) => false);
                    builder.CloseComponent();
                });
            });

            var groups = component.FindAll("[role=group]");

            Assert.NotEmpty(groups);
        }

        [Fact]
        public void Tree_Renders_AriaSelectedOnItems()
        {
            using var ctx = new TestContext();
            var data = new List<Category>
            {
                new Category { Name = "Electronics" }
            };

            var component = ctx.RenderComponent<RadzenTree>(parameters =>
            {
                parameters.Add(p => p.Data, data);
                parameters.Add(p => p.ChildContent, builder =>
                {
                    builder.OpenComponent<RadzenTreeLevel>(0);
                    builder.AddAttribute(1, "TextProperty", "Name");
                    builder.CloseComponent();
                });
            });

            var item = component.Find("[role=treeitem]");

            Assert.Equal("false", item.GetAttribute("aria-selected"));
        }

        [Fact]
        public void Tree_Exposes_ActiveDescendant_AsFocusMoves()
        {
            using var ctx = new TestContext();
            var data = new List<Category>
            {
                new Category { Name = "Electronics" },
                new Category { Name = "Clothing" }
            };

            var component = ctx.RenderComponent<RadzenTree>(parameters =>
            {
                parameters.Add(p => p.Data, data);
                parameters.Add(p => p.ChildContent, builder =>
                {
                    builder.OpenComponent<RadzenTreeLevel>(0);
                    builder.AddAttribute(1, "TextProperty", "Name");
                    builder.CloseComponent();
                });
            });

            var container = component.Find("[role=tree]");
            var items = component.FindAll("[role=treeitem]");

            var firstId = items.First().GetAttribute("id");
            Assert.Equal(firstId, container.GetAttribute("aria-activedescendant"));

            container.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Code = "ArrowDown" });

            container = component.Find("[role=tree]");
            var secondId = component.FindAll("[role=treeitem]").Last().GetAttribute("id");

            Assert.NotEqual(firstId, secondId);
            Assert.Equal(secondId, container.GetAttribute("aria-activedescendant"));
        }

        [Fact]
        public void Tree_HomeEnd_MoveActiveDescendant()
        {
            using var ctx = new TestContext();
            var data = new List<Category>
            {
                new Category { Name = "Electronics" },
                new Category { Name = "Clothing" },
                new Category { Name = "Books" }
            };

            var component = ctx.RenderComponent<RadzenTree>(parameters =>
            {
                parameters.Add(p => p.Data, data);
                parameters.Add(p => p.ChildContent, builder =>
                {
                    builder.OpenComponent<RadzenTreeLevel>(0);
                    builder.AddAttribute(1, "TextProperty", "Name");
                    builder.CloseComponent();
                });
            });

            var items = component.FindAll("[role=treeitem]");
            var firstId = items.First().GetAttribute("id");
            var lastId = items.Last().GetAttribute("id");

            var container = component.Find("[role=tree]");
            container.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Code = "End" });

            container = component.Find("[role=tree]");
            Assert.Equal(lastId, container.GetAttribute("aria-activedescendant"));

            container.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Code = "Home" });

            container = component.Find("[role=tree]");
            Assert.Equal(firstId, container.GetAttribute("aria-activedescendant"));
        }

        [Fact]
        public void Tree_ArrowRight_ExpandsThenMovesToFirstChild()
        {
            using var ctx = new TestContext();
            var data = new List<Category>
            {
                new Category
                {
                    Name = "Electronics",
                    Products = new List<Product>
                    {
                        new Product { Name = "Laptop" }
                    }
                }
            };

            var component = ctx.RenderComponent<RadzenTree>(parameters =>
            {
                parameters.Add(p => p.Data, data);
                parameters.Add(p => p.ChildContent, builder =>
                {
                    builder.OpenComponent<RadzenTreeLevel>(0);
                    builder.AddAttribute(1, "TextProperty", "Name");
                    builder.AddAttribute(2, "ChildrenProperty", "Products");
                    builder.AddAttribute(3, "HasChildren", (object c) => c is Category);
                    builder.CloseComponent();

                    builder.OpenComponent<RadzenTreeLevel>(4);
                    builder.AddAttribute(5, "TextProperty", "Name");
                    builder.AddAttribute(6, "HasChildren", (object product) => false);
                    builder.CloseComponent();
                });
            });

            var container = component.Find("[role=tree]");
            var rootId = component.FindAll("[role=treeitem]").First().GetAttribute("id");
            Assert.Equal(rootId, container.GetAttribute("aria-activedescendant"));

            container.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Code = "ArrowRight" });

            container = component.Find("[role=tree]");
            Assert.Equal(rootId, container.GetAttribute("aria-activedescendant"));

            container.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Code = "ArrowRight" });

            container = component.Find("[role=tree]");
            var childId = component.FindAll("[role=treeitem]").Last().GetAttribute("id");

            Assert.NotEqual(rootId, childId);
            Assert.Equal(childId, container.GetAttribute("aria-activedescendant"));
        }

        [Fact]
        public void Tree_ArrowLeft_CollapsesThenMovesToParent()
        {
            using var ctx = new TestContext();
            var data = new List<Category>
            {
                new Category
                {
                    Name = "Electronics",
                    Products = new List<Product>
                    {
                        new Product { Name = "Laptop" }
                    }
                }
            };

            var component = ctx.RenderComponent<RadzenTree>(parameters =>
            {
                parameters.Add(p => p.Data, data);
                parameters.Add(p => p.ChildContent, builder =>
                {
                    builder.OpenComponent<RadzenTreeLevel>(0);
                    builder.AddAttribute(1, "TextProperty", "Name");
                    builder.AddAttribute(2, "ChildrenProperty", "Products");
                    builder.AddAttribute(3, "Expanded", (object c) => true);
                    builder.AddAttribute(4, "HasChildren", (object c) => c is Category);
                    builder.CloseComponent();

                    builder.OpenComponent<RadzenTreeLevel>(5);
                    builder.AddAttribute(6, "TextProperty", "Name");
                    builder.AddAttribute(7, "HasChildren", (object product) => false);
                    builder.CloseComponent();
                });
            });

            var container = component.Find("[role=tree]");
            var rootId = component.FindAll("[role=treeitem]").First().GetAttribute("id");

            container.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Code = "ArrowDown" });

            container = component.Find("[role=tree]");
            var childId = component.FindAll("[role=treeitem]").Last().GetAttribute("id");
            Assert.Equal(childId, container.GetAttribute("aria-activedescendant"));

            container.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Code = "ArrowLeft" });

            container = component.Find("[role=tree]");
            Assert.Equal(rootId, container.GetAttribute("aria-activedescendant"));
        }

        [Fact]
        public void Tree_TypeAhead_MovesActiveDescendant()
        {
            using var ctx = new TestContext();
            var data = new List<Category>
            {
                new Category { Name = "Electronics" },
                new Category { Name = "Clothing" },
                new Category { Name = "Books" }
            };

            var component = ctx.RenderComponent<RadzenTree>(parameters =>
            {
                parameters.Add(p => p.Data, data);
                parameters.Add(p => p.ChildContent, builder =>
                {
                    builder.OpenComponent<RadzenTreeLevel>(0);
                    builder.AddAttribute(1, "TextProperty", "Name");
                    builder.CloseComponent();
                });
            });

            var items = component.FindAll("[role=treeitem]");
            var booksId = items.Last().GetAttribute("id");

            var container = component.Find("[role=tree]");
            container.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "b" });

            container = component.Find("[role=tree]");
            Assert.Equal(booksId, container.GetAttribute("aria-activedescendant"));
        }

        [Fact]
        public void Tree_Enter_TogglesExpandableNode()
        {
            using var ctx = new TestContext();
            var data = new List<Category>
            {
                new Category
                {
                    Name = "Electronics",
                    Products = new List<Product>
                    {
                        new Product { Name = "Laptop" }
                    }
                }
            };

            var component = ctx.RenderComponent<RadzenTree>(parameters =>
            {
                parameters.Add(p => p.Data, data);
                parameters.Add(p => p.ChildContent, builder =>
                {
                    builder.OpenComponent<RadzenTreeLevel>(0);
                    builder.AddAttribute(1, "TextProperty", "Name");
                    builder.AddAttribute(2, "ChildrenProperty", "Products");
                    builder.AddAttribute(3, "HasChildren", (object c) => c is Category);
                    builder.CloseComponent();

                    builder.OpenComponent<RadzenTreeLevel>(4);
                    builder.AddAttribute(5, "TextProperty", "Name");
                    builder.AddAttribute(6, "HasChildren", (object product) => false);
                    builder.CloseComponent();
                });
            });

            var root = component.Find("[role=treeitem]");
            Assert.Equal("false", root.GetAttribute("aria-expanded"));

            var container = component.Find("[role=tree]");
            container.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Code = "Enter" });

            root = component.Find("[role=treeitem]");
            Assert.Equal("true", root.GetAttribute("aria-expanded"));
        }

        [Fact]
        public void Tree_DoesNotRender_AriaSelected_WhenCheckBoxesAllowed()
        {
            using var ctx = new TestContext();
            var data = new List<Category>
            {
                new Category { Name = "Electronics" }
            };

            var component = ctx.RenderComponent<RadzenTree>(parameters =>
            {
                parameters.Add(p => p.AllowCheckBoxes, true);
                parameters.Add(p => p.Data, data);
                parameters.Add(p => p.ChildContent, builder =>
                {
                    builder.OpenComponent<RadzenTreeLevel>(0);
                    builder.AddAttribute(1, "TextProperty", "Name");
                    builder.CloseComponent();
                });
            });

            var item = component.Find("[role=treeitem]");

            Assert.False(item.HasAttribute("aria-selected"));
        }
    }
}

