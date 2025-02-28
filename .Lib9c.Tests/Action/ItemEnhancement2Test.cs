namespace Lib9c.Tests.Action
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Globalization;
    using System.Linq;
    using Libplanet;
    using Libplanet.Action;
    using Libplanet.Assets;
    using Libplanet.Crypto;
    using Nekoyume;
    using Nekoyume.Action;
    using Nekoyume.Model.Item;
    using Nekoyume.Model.State;
    using Xunit;

    public class ItemEnhancement2Test
    {
        private readonly IRandom _random;
        private readonly TableSheets _tableSheets;
        private readonly Address _agentAddress;
        private readonly Address _avatarAddress;
        private readonly Address _slotAddress;
        private readonly AvatarState _avatarState;
        private readonly Currency _currency;
        private IAccountStateDelta _initialState;

        public ItemEnhancement2Test()
        {
            var sheets = TableSheetsImporter.ImportSheets();
            _random = new TestRandom();
            _tableSheets = new TableSheets(sheets);
            var privateKey = new PrivateKey();
            _agentAddress = privateKey.PublicKey.ToAddress();
            var agentState = new AgentState(_agentAddress);

            _avatarAddress = _agentAddress.Derive("avatar");
            _avatarState = new AvatarState(
                _avatarAddress,
                _agentAddress,
                0,
                _tableSheets.GetAvatarSheets(),
                new GameConfigState(),
                default
            );

            agentState.avatarAddresses.Add(0, _avatarAddress);

            _currency = new Currency("NCG", 2, minter: null);
            var gold = new GoldCurrencyState(_currency);
            _slotAddress =
                _avatarAddress.Derive(string.Format(CultureInfo.InvariantCulture, CombinationSlotState.DeriveFormat, 0));

            _initialState = new State()
                .SetState(_agentAddress, agentState.Serialize())
                .SetState(_avatarAddress, _avatarState.Serialize())
                .SetState(_slotAddress, new CombinationSlotState(_slotAddress, 0).Serialize())
                .SetState(GoldCurrencyState.Address, gold.Serialize())
                .MintAsset(GoldCurrencyState.Address, gold.Currency * 100000000000)
                .TransferAsset(Addresses.GoldCurrency, _agentAddress, gold.Currency * 1000);

            Assert.Equal(gold.Currency * 99999999000, _initialState.GetBalance(Addresses.GoldCurrency, gold.Currency));
            Assert.Equal(gold.Currency * 1000, _initialState.GetBalance(_agentAddress, gold.Currency));

            foreach (var (key, value) in sheets)
            {
                _initialState = _initialState.SetState(Addresses.TableSheet.Derive(key), value.Serialize());
            }
        }

        [Theory]
        [InlineData(0, 1, 1000)]
        [InlineData(3, 4, 0)]
        public void Execute(int level, int expectedLevel, int expectedGold)
        {
            var row = _tableSheets.EquipmentItemSheet.Values.First(r => r.Grade == 1);
            var equipment = (Equipment)ItemFactory.CreateItemUsable(row, default, 0, level);
            var materialId = Guid.NewGuid();
            var material = (Equipment)ItemFactory.CreateItemUsable(row, materialId, 0, level);

            _avatarState.inventory.AddItem(equipment, count: 1);
            _avatarState.inventory.AddItem(material, count: 1);

            _avatarState.worldInformation.ClearStage(1, 1, 1, _tableSheets.WorldSheet, _tableSheets.WorldUnlockSheet);

            var slotAddress =
                _avatarAddress.Derive(string.Format(CultureInfo.InvariantCulture, CombinationSlotState.DeriveFormat, 0));

            Assert.Equal(level, equipment.level);

            _initialState = _initialState.SetState(_avatarAddress, _avatarState.Serialize());

            var action = new ItemEnhancement2()
            {
                itemId = default,
                materialId = materialId,
                avatarAddress = _avatarAddress,
                slotIndex = 0,
            };

            var nextState = action.Execute(new ActionContext()
            {
                PreviousStates = _initialState,
                Signer = _agentAddress,
                BlockIndex = 1,
                Random = _random,
            });

            var slotState = nextState.GetCombinationSlotState(_avatarAddress, 0);
            var resultEquipment = (Equipment)slotState.Result.itemUsable;
            Assert.Equal(expectedLevel, resultEquipment.level);
            Assert.Equal(default, resultEquipment.ItemId);
            Assert.Equal(expectedGold * _currency, nextState.GetBalance(_agentAddress, _currency));
            Assert.Equal(
                (1000 - expectedGold) * _currency,
                nextState.GetBalance(Addresses.Blacksmith, _currency)
            );
        }

        [Fact]
        public void ExecuteThrowFailedLoadStateException()
        {
            var action = new ItemEnhancement2()
            {
                itemId = default,
                materialId = default,
                avatarAddress = _avatarAddress,
                slotIndex = 0,
            };

            Assert.Throws<FailedLoadStateException>(() => action.Execute(new ActionContext()
                {
                    PreviousStates = new State(),
                    Signer = _agentAddress,
                    BlockIndex = 0,
                })
            );
        }

        [Fact]
        public void ExecuteThrowItemDoesNotExistException()
        {
            var action = new ItemEnhancement2()
            {
                itemId = default,
                materialId = default,
                avatarAddress = _avatarAddress,
                slotIndex = 0,
            };

            Assert.Throws<ItemDoesNotExistException>(() => action.Execute(new ActionContext()
                {
                    PreviousStates = _initialState,
                    Signer = _agentAddress,
                    BlockIndex = 0,
                })
            );
        }

        [Fact]
        public void ExecuteThrowRequiredBlockIndexException()
        {
            var row = _tableSheets.EquipmentItemSheet.Values.First(r => r.Grade == 1);
            var equipment = (Equipment)ItemFactory.CreateItemUsable(row, default, 100, 1);

            _avatarState.inventory.AddItem(equipment, count: 1);

            _initialState = _initialState.SetState(_avatarAddress, _avatarState.Serialize());

            var action = new ItemEnhancement2()
            {
                itemId = equipment.ItemId,
                materialId = default,
                avatarAddress = _avatarAddress,
                slotIndex = 0,
            };

            Assert.Throws<RequiredBlockIndexException>(() => action.Execute(new ActionContext()
                {
                    PreviousStates = _initialState,
                    Signer = _agentAddress,
                    BlockIndex = 0,
                })
            );
        }

        [Fact]
        public void ExecuteThrowInvalidCastException()
        {
            var row = _tableSheets.ConsumableItemSheet.Values.First(r => r.Grade == 1);
            var consumable = (Consumable)ItemFactory.CreateItemUsable(row, default, 0, 1);

            _avatarState.inventory.AddItem(consumable, count: 1);

            _initialState = _initialState.SetState(_avatarAddress, _avatarState.Serialize());

            var action = new ItemEnhancement2()
            {
                itemId = consumable.ItemId,
                materialId = default,
                avatarAddress = _avatarAddress,
                slotIndex = 0,
            };

            Assert.Throws<InvalidCastException>(() => action.Execute(new ActionContext()
                {
                    PreviousStates = _initialState,
                    Signer = _agentAddress,
                    BlockIndex = 0,
                })
            );
        }

        [Fact]
        public void ExecuteThrowCombinationSlotUnlockException()
        {
            var row = _tableSheets.EquipmentItemSheet.Values.First(r => r.Grade == 1);
            var equipment = (Equipment)ItemFactory.CreateItemUsable(row, default, 0, 1);

            _avatarState.inventory.AddItem(equipment, count: 1);

            _initialState = _initialState
                .SetState(_avatarAddress, _avatarState.Serialize())
                .SetState(_slotAddress, new CombinationSlotState(_slotAddress, 100).Serialize());

            var action = new ItemEnhancement2()
            {
                itemId = equipment.ItemId,
                materialId = default,
                avatarAddress = _avatarAddress,
                slotIndex = 0,
            };

            Assert.Throws<CombinationSlotUnlockException>(() => action.Execute(new ActionContext()
                {
                    PreviousStates = _initialState,
                    Signer = _agentAddress,
                    BlockIndex = 0,
                })
            );
        }

        [Fact]
        public void ExecuteThrowEquipmentLevelExceededException()
        {
            var row = _tableSheets.EquipmentItemSheet.Values.First(r => r.Grade == 1);
            var equipment = (Equipment)ItemFactory.CreateItemUsable(row, default, 0, 10);
            var materialId = Guid.NewGuid();
            var material = (Equipment)ItemFactory.CreateItemUsable(row, materialId, 0);

            _avatarState.inventory.AddItem(equipment, count: 1);
            _avatarState.inventory.AddItem(material, count: 1);

            _avatarState.worldInformation.ClearStage(1, 1, 1, _tableSheets.WorldSheet, _tableSheets.WorldUnlockSheet);

            _initialState = _initialState.SetState(_avatarAddress, _avatarState.Serialize());

            var action = new ItemEnhancement2()
            {
                itemId = equipment.ItemId,
                materialId = materialId,
                avatarAddress = _avatarAddress,
                slotIndex = 0,
            };

            Assert.Throws<EquipmentLevelExceededException>(() => action.Execute(new ActionContext()
                {
                    PreviousStates = _initialState,
                    Signer = _agentAddress,
                    BlockIndex = 0,
                })
            );
        }

        [Fact]
        public void ExecuteThrowNotEnoughMaterialException()
        {
            var row = _tableSheets.EquipmentItemSheet.Values.First(r => r.Grade == 1);
            var equipment = (Equipment)ItemFactory.CreateItemUsable(row, default, 0);
            var materialId = Guid.NewGuid();

            _avatarState.inventory.AddItem(equipment);

            _avatarState.worldInformation.ClearStage(1, 1, 1, _tableSheets.WorldSheet, _tableSheets.WorldUnlockSheet);

            _initialState = _initialState.SetState(_avatarAddress, _avatarState.Serialize());

            var action = new ItemEnhancement2()
            {
                itemId = equipment.ItemId,
                materialId = materialId,
                avatarAddress = _avatarAddress,
                slotIndex = 0,
            };

            Assert.Throws<NotEnoughMaterialException>(() => action.Execute(new ActionContext()
                {
                    PreviousStates = _initialState,
                    Signer = _agentAddress,
                    BlockIndex = 0,
                })
            );
        }

        [Theory]
        [InlineData(
            "F9168C5E-CEB2-4faa-B6BF-329BF39FA1E4",
            ItemSubType.Weapon,
            1,
            1,
            "F9168C5E-CEB2-4faa-B6BF-329BF39FA1E4",
            ItemSubType.Weapon,
            1,
            1
        )]
        [InlineData(
            "F9168C5E-CEB2-4faa-B6BF-329BF39FA1E4",
            ItemSubType.Weapon,
            1,
            1,
            "936DA01F-9ABD-4d9d-80C7-02AF85C822A8",
            ItemSubType.Armor,
            1,
            1
        )]
        [InlineData(
            "F9168C5E-CEB2-4faa-B6BF-329BF39FA1E4",
            ItemSubType.Weapon,
            1,
            1,
            "936DA01F-9ABD-4d9d-80C7-02AF85C822A8",
            ItemSubType.Weapon,
            2,
            1
        )]
        [InlineData(
            "F9168C5E-CEB2-4faa-B6BF-329BF39FA1E4",
            ItemSubType.Weapon,
            1,
            2,
            "936DA01F-9ABD-4d9d-80C7-02AF85C822A8",
            ItemSubType.Weapon,
            1,
            1
        )]
        public void ExecuteThrowInvalidMaterialException(
            string equipmentGuid,
            ItemSubType equipmentSubType,
            int equipmentGrade,
            int equipmentLevel,
            string materialGuid,
            ItemSubType materialSubType,
            int materialGrade,
            int materialLevel
        )
        {
            var equipmentRow = _tableSheets.EquipmentItemSheet.Values.First(r =>
                r.Grade == equipmentGrade && r.ItemSubType == equipmentSubType);
            var materialRow = _tableSheets.EquipmentItemSheet.Values.First(r =>
                r.Grade == materialGrade && r.ItemSubType == materialSubType);
            var equipment = (Equipment)ItemFactory.CreateItemUsable(equipmentRow, new Guid(equipmentGuid), 0, equipmentLevel);
            var materialId = new Guid(materialGuid);
            var material = (Equipment)ItemFactory.CreateItemUsable(materialRow, materialId, 0, materialLevel);

            _avatarState.inventory.AddItem(equipment);
            _avatarState.inventory.AddItem(material);

            _avatarState.worldInformation.ClearStage(1, 1, 1, _tableSheets.WorldSheet, _tableSheets.WorldUnlockSheet);

            _initialState = _initialState.SetState(_avatarAddress, _avatarState.Serialize());

            var action = new ItemEnhancement2()
            {
                itemId = equipment.ItemId,
                materialId = materialId,
                avatarAddress = _avatarAddress,
                slotIndex = 0,
            };

            Assert.Throws<InvalidMaterialException>(() => action.Execute(new ActionContext()
                {
                    PreviousStates = _initialState,
                    Signer = _agentAddress,
                    BlockIndex = 0,
                })
            );
        }

        [Fact]
        public void Deterministic()
        {
            var guid1 = new Guid("F9168C5E-CEB2-4faa-B6BF-329BF39FA1E4");

            var action = new ItemEnhancement2()
            {
                itemId = default,
                materialId = guid1,
                avatarAddress = default,
                slotIndex = 0,
            };

            var action2 = new ItemEnhancement2();
            action2.LoadPlainValue(action.PlainValue);
            action2.materialId = guid1;

            Assert.Equal(action.PlainValue, action2.PlainValue);
        }

        [Fact]
        public void ResultModelDeterministic()
        {
            var guid1 = new Guid("F9168C5E-CEB2-4faa-B6BF-329BF39FA1E4");

            var row = _tableSheets.EquipmentItemSheet.Values.First();
            var itemUsable = ItemFactory.CreateItemUsable(row, default, 0);
            var result = new ItemEnhancement7.ResultModel()
            {
                id = default,
                materialItemIdList = new[] { guid1 },
                gold = 0,
                actionPoint = 0,
                itemUsable = itemUsable,
            };

            var result2 = new ItemEnhancement7.ResultModel()
            {
                id = default,
                materialItemIdList = new[] { guid1 },
                gold = 0,
                actionPoint = 0,
                itemUsable = itemUsable,
            };

            Assert.Equal(result.Serialize(), result2.Serialize());
        }

        [Fact]
        public void Rehearsal()
        {
            var agentAddress = default(Address);
            var avatarAddress = agentAddress.Derive("avatar");
            var slotAddress =
                avatarAddress.Derive(string.Format(CultureInfo.InvariantCulture, CombinationSlotState.DeriveFormat, 0));

            var action = new ItemEnhancement2()
            {
                itemId = default,
                materialId = Guid.NewGuid(),
                avatarAddress = avatarAddress,
                slotIndex = 0,
            };

            var gold = new GoldCurrencyState(new Currency("NCG", 2, minter: null));

            var updatedAddresses = new List<Address>()
            {
                agentAddress,
                avatarAddress,
                slotAddress,
                Addresses.GoldCurrency,
                Addresses.Blacksmith,
            };

            var state = new State()
                .SetState(GoldCurrencyState.Address, gold.Serialize());

            var nextState = action.Execute(new ActionContext()
            {
                PreviousStates = state,
                Signer = agentAddress,
                BlockIndex = 0,
                Rehearsal = true,
            });

            Assert.Equal(updatedAddresses.ToImmutableHashSet(), nextState.UpdatedAddresses);
        }
    }
}
